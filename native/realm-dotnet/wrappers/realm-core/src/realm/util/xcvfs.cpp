// xc-osu! xcvfs — see xcvfs.hpp. xc softworks (kudasaixc)
#include <realm/util/xcvfs.hpp>

#if REALM_XCVFS
#include <cerrno>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <malloc.h>
#include <map>
#include <mutex>
#include <string>
#include <sys/file.h>
#include <sys/mman.h>
#include <unistd.h>
#include <unordered_map>

namespace realm::xcvfs {
namespace {

constexpr size_t k_align = 65536; // == sysconf(_SC_PAGESIZE) on emscripten

struct Image {
    dev_t dev;
    ino_t ino;
    std::string path;
    int wfd = -1; // writable descriptor used for write-back
    char* base = nullptr;
    size_t size = 0;
    size_t cap = 0;
    size_t dirty_lo = SIZE_MAX, dirty_hi = 0;
    size_t synced_size = 0;
    bool mapped_writable = false;
    int nfds = 0;
    int nmaps = 0;
    int shared_locks = 0;
    int excl_owner = -1;
};

struct FdInfo {
    Image* img;
    off_t pos = 0;
    int lock = 0; // 0 none, 1 shared, 2 exclusive
};

std::mutex g_mutex;
std::condition_variable g_lock_cv;
std::map<std::pair<dev_t, ino_t>, Image*> g_images;
std::unordered_map<int, FdInfo> g_fds;

bool ends_with(const std::string& s, const char* suffix)
{
    size_t n = strlen(suffix);
    return s.size() >= n && s.compare(s.size() - n, n, suffix) == 0;
}

size_t round_up(size_t v)
{
    return (v + k_align - 1) & ~(k_align - 1);
}

// Capacity is reserved up-front because mapped addresses must stay valid while the file grows.
// Untouched pages of the wasm heap are never committed by the browser, so this costs address space, not RAM.
size_t initial_capacity(const std::string& path, size_t size)
{
    if (ends_with(path, ".lock") || ends_with(path, ".note"))
        return round_up(std::max<size_t>(size * 2, 1 << 20));
    if (ends_with(path, ".realm"))
        return round_up(std::max<size_t>(size * 2 + (64u << 20), 256u << 20));
    return round_up(std::max<size_t>(size * 2, 16u << 20));
}

bool allocate(Image* img, size_t want)
{
    // try the preferred capacity first, then progressively smaller ones (never below what's needed)
    size_t needed = round_up(std::max<size_t>(img->size, 1));
    for (size_t cap = round_up(want); cap >= needed; cap = round_up(cap / 2 + needed / 2)) {
        void* p = memalign(k_align, cap);
        if (p) {
            if (img->base) {
                memcpy(p, img->base, img->size);
                free(img->base);
            }
            img->base = static_cast<char*>(p);
            img->cap = cap;
            return true;
        }
        if (cap == needed)
            break;
    }
    return false;
}

bool ensure_capacity(Image* img, size_t needed)
{
    if (needed <= img->cap)
        return true;
    if (img->nmaps > 0)
        return false; // cannot move memory that is mapped
    return allocate(img, std::max(needed * 2, img->cap * 2));
}

void mark_dirty(Image* img, size_t lo, size_t hi)
{
    if (lo < img->dirty_lo)
        img->dirty_lo = lo;
    if (hi > img->dirty_hi)
        img->dirty_hi = hi;
}

int flush(Image* img)
{
    if (img->wfd < 0)
        return 0;
    if (img->dirty_hi > img->size)
        img->dirty_hi = img->size;
    if (img->dirty_lo < img->dirty_hi) {
        size_t off = img->dirty_lo, n = img->dirty_hi - img->dirty_lo;
        while (n > 0) {
            ssize_t r = ::pwrite(img->wfd, img->base + off, n, static_cast<off_t>(off));
            if (r < 0) {
                if (errno == EINTR)
                    continue;
                return -1;
            }
            off += size_t(r);
            n -= size_t(r);
        }
    }
    if (img->synced_size != img->size) {
        if (::ftruncate(img->wfd, static_cast<off_t>(img->size)) != 0)
            return -1;
        img->synced_size = img->size;
    }
    img->dirty_lo = SIZE_MAX;
    img->dirty_hi = 0;
    return 0;
}

void maybe_release(Image* img)
{
    if (img->nfds > 0 || img->nmaps > 0)
        return;
    if (img->mapped_writable)
        mark_dirty(img, 0, img->size); // writes through mappings aren't tracked; persist everything
    flush(img);
    if (img->wfd >= 0)
        ::close(img->wfd);
    g_images.erase({img->dev, img->ino});
    free(img->base);
    delete img;
}

Image* image_containing(const void* addr)
{
    auto a = static_cast<const char*>(addr);
    for (auto& [key, img] : g_images)
        if (a >= img->base && a < img->base + img->cap)
            return img;
    return nullptr;
}

FdInfo* fd_info(int fd)
{
    auto it = g_fds.find(fd);
    return it == g_fds.end() ? nullptr : &it->second;
}

void release_lock(int fd, FdInfo& f)
{
    Image* img = f.img;
    if (f.lock == 1)
        img->shared_locks--;
    else if (f.lock == 2 && img->excl_owner == fd)
        img->excl_owner = -1;
    if (f.lock)
        g_lock_cv.notify_all();
    f.lock = 0;
}

} // namespace

int open(const char* path, int flags, mode_t mode)
{
    int fd = ::open(path, flags, mode);
    if (fd < 0)
        return fd;
    struct ::stat st;
    if (::fstat(fd, &st) != 0 || !S_ISREG(st.st_mode))
        return fd; // directories, fifos etc. pass straight through

    std::lock_guard<std::mutex> lk(g_mutex);
    auto key = std::make_pair(st.st_dev, st.st_ino);
    Image* img;
    auto it = g_images.find(key);
    if (it != g_images.end()) {
        img = it->second;
        if (flags & O_TRUNC) {
            img->size = 0;
            img->synced_size = 0;
            img->dirty_lo = SIZE_MAX;
            img->dirty_hi = 0;
        }
    }
    else {
        img = new Image();
        img->dev = st.st_dev;
        img->ino = st.st_ino;
        img->path = path;
        img->size = static_cast<size_t>(st.st_size);
        img->synced_size = img->size;
        if (!allocate(img, initial_capacity(img->path, img->size))) {
            delete img;
            ::close(fd);
            errno = ENOMEM;
            return -1;
        }
        size_t off = 0;
        while (off < img->size) {
            ssize_t r = ::pread(fd, img->base + off, img->size - off, static_cast<off_t>(off));
            if (r <= 0) {
                if (r < 0 && errno == EINTR)
                    continue;
                break;
            }
            off += size_t(r);
        }
        g_images[key] = img;
    }
    if (img->wfd < 0 && (flags & O_ACCMODE) != O_RDONLY)
        img->wfd = ::dup(fd);
    img->nfds++;
    g_fds[fd] = FdInfo{img};
    return fd;
}

int close(int fd)
{
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        if (auto f = fd_info(fd)) {
            Image* img = f->img;
            release_lock(fd, *f);
            g_fds.erase(fd);
            img->nfds--;
            maybe_release(img);
        }
    }
    return ::close(fd);
}

ssize_t pread(int fd, void* buf, size_t n, off_t off)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return ::pread(fd, buf, n, off);
    Image* img = f->img;
    if (off < 0) {
        errno = EINVAL;
        return -1;
    }
    if (size_t(off) >= img->size)
        return 0;
    size_t len = std::min(n, img->size - size_t(off));
    memcpy(buf, img->base + off, len);
    return ssize_t(len);
}

ssize_t pwrite(int fd, const void* buf, size_t n, off_t off)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return ::pwrite(fd, buf, n, off);
    Image* img = f->img;
    if (off < 0) {
        errno = EINVAL;
        return -1;
    }
    size_t end = size_t(off) + n;
    if (!ensure_capacity(img, end)) {
        errno = ENOSPC;
        return -1;
    }
    if (size_t(off) > img->size)
        memset(img->base + img->size, 0, size_t(off) - img->size);
    memcpy(img->base + off, buf, n);
    if (end > img->size)
        img->size = end;
    mark_dirty(img, size_t(off), end);
    return ssize_t(n);
}

ssize_t read(int fd, void* buf, size_t n)
{
    off_t pos;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto f = fd_info(fd);
        if (!f)
            return ::read(fd, buf, n);
        pos = f->pos;
    }
    ssize_t r = pread(fd, buf, n, pos);
    if (r > 0) {
        std::lock_guard<std::mutex> lk(g_mutex);
        if (auto f = fd_info(fd))
            f->pos = pos + r;
    }
    return r;
}

ssize_t write(int fd, const void* buf, size_t n)
{
    off_t pos;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto f = fd_info(fd);
        if (!f)
            return ::write(fd, buf, n);
        pos = f->pos;
    }
    ssize_t r = pwrite(fd, buf, n, pos);
    if (r > 0) {
        std::lock_guard<std::mutex> lk(g_mutex);
        if (auto f = fd_info(fd))
            f->pos = pos + r;
    }
    return r;
}

off_t lseek(int fd, off_t off, int whence)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return ::lseek(fd, off, whence);
    off_t base = whence == SEEK_SET ? 0 : whence == SEEK_CUR ? f->pos : off_t(f->img->size);
    off_t p = base + off;
    if (p < 0) {
        errno = EINVAL;
        return -1;
    }
    f->pos = p;
    return p;
}

int ftruncate(int fd, off_t size)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return ::ftruncate(fd, size);
    Image* img = f->img;
    if (size < 0) {
        errno = EINVAL;
        return -1;
    }
    size_t s = size_t(size);
    if (!ensure_capacity(img, s)) {
        errno = ENOSPC;
        return -1;
    }
    if (s > img->size)
        memset(img->base + img->size, 0, s - img->size);
    img->size = s;
    return 0;
}

// Returns an error code like the POSIX function (does not set errno). Grows the coherent image, never shrinks.
int posix_fallocate(int fd, off_t offset, off_t len)
{
    if (offset < 0 || len <= 0)
        return EINVAL;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto f = fd_info(fd);
        if (!f)
            return ::posix_fallocate(fd, offset, len);
        if (size_t(offset + len) <= f->img->size)
            return 0;
    }
    return realm::xcvfs::ftruncate(fd, offset + len) == 0 ? 0 : ENOSPC;
}

int fstat(int fd, struct stat* st)
{
    int r = ::fstat(fd, st);
    if (r != 0)
        return r;
    std::lock_guard<std::mutex> lk(g_mutex);
    if (auto f = fd_info(fd))
        st->st_size = off_t(f->img->size);
    return 0;
}

int stat(const char* path, struct stat* st)
{
    int r = ::stat(path, st);
    if (r != 0)
        return r;
    std::lock_guard<std::mutex> lk(g_mutex);
    auto it = g_images.find({st->st_dev, st->st_ino});
    if (it != g_images.end())
        st->st_size = off_t(it->second->size);
    return 0;
}

int fsync(int fd)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return ::fsync(fd);
    return flush(f->img);
}

int flock(int fd, int op)
{
    std::unique_lock<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f)
        return 0;
    Image* img = f->img;
    bool nb = (op & LOCK_NB) != 0;
    op &= ~LOCK_NB;

    if (op == LOCK_UN) {
        release_lock(fd, *f);
        return 0;
    }
    if (op == LOCK_SH) {
        if (f->lock == 1)
            return 0;
        if (f->lock == 2)
            release_lock(fd, *f); // conversion is not atomic, as on Linux
        auto can = [&] { return img->excl_owner == -1; };
        if (!can()) {
            if (nb) {
                errno = EWOULDBLOCK;
                return -1;
            }
            g_lock_cv.wait(lk, [&] { return can() || !fd_info(fd); });
            f = fd_info(fd);
            if (!f) {
                errno = EBADF;
                return -1;
            }
        }
        img->shared_locks++;
        f->lock = 1;
        return 0;
    }
    if (op == LOCK_EX) {
        if (f->lock == 2)
            return 0;
        if (f->lock == 1)
            release_lock(fd, *f);
        auto can = [&] { return img->excl_owner == -1 && img->shared_locks == 0; };
        if (!can()) {
            if (nb) {
                errno = EWOULDBLOCK;
                return -1;
            }
            g_lock_cv.wait(lk, [&] { return can() || !fd_info(fd); });
            f = fd_info(fd);
            if (!f) {
                errno = EBADF;
                return -1;
            }
        }
        img->excl_owner = fd;
        f->lock = 2;
        return 0;
    }
    errno = EINVAL;
    return -1;
}

void* mmap(void* addr, size_t len, int prot, int flags, int fd, off_t off)
{
    if (flags & MAP_ANONYMOUS) {
        if (prot == PROT_NONE) {
            // address-space reservations can't be honoured in wasm; realm falls back to plain mappings
            errno = ENOMEM;
            return MAP_FAILED;
        }
        return ::mmap(addr, len, prot, flags, fd, off);
    }
    std::lock_guard<std::mutex> lk(g_mutex);
    auto f = fd_info(fd);
    if (!f || !(flags & MAP_SHARED))
        return ::mmap(addr, len, prot, flags, fd, off);
    Image* img = f->img;
    size_t end = size_t(off) + len;
    if (!ensure_capacity(img, end)) {
        errno = ENOMEM;
        return MAP_FAILED;
    }
    char* p = img->base + off;
    if ((flags & MAP_FIXED) && addr != p) {
        errno = EINVAL;
        return MAP_FAILED;
    }
    img->nmaps++;
    if (prot & PROT_WRITE)
        img->mapped_writable = true;
    return p;
}

int munmap(void* addr, size_t len)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    if (Image* img = image_containing(addr)) {
        img->nmaps--;
        maybe_release(img);
        return 0;
    }
    return ::munmap(addr, len);
}

int msync(void* addr, size_t len, int flags)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    if (Image* img = image_containing(addr)) {
        size_t lo = size_t(static_cast<char*>(addr) - img->base);
        mark_dirty(img, lo, lo + len);
        return flush(img);
    }
    return ::msync(addr, len, flags);
}

int unlink(const char* path)
{
    return ::unlink(path);
}

} // namespace realm::xcvfs
#endif
