// xc-osu! xcvfs — a coherent file layer for realm-core on multi-threaded WebAssembly. xc softworks (kudasaixc)
//
// Emscripten's mmap(MAP_SHARED) copies file contents into a private buffer, so two mappings of the same file
// (realm maps its data and lock files many times, from many threads) never see each other's writes, and flock()
// is a no-op. Realm needs a unified page cache and real advisory locks, so realm's file syscalls are routed here:
//  * each open inode gets one contiguous in-memory image; every read/write/mmap of that inode uses it,
//    so all mappings alias and are always coherent (like an OS page cache);
//  * msync()/fsync() write dirty ranges back to the real (IDBFS-persisted) file;
//  * flock() is implemented with in-process shared/exclusive semantics per open file description.
#pragma once

#if defined(__EMSCRIPTEN__) && defined(__EMSCRIPTEN_PTHREADS__)
#define REALM_XCVFS 1

#include <sys/stat.h>
#include <sys/types.h>
#include <stddef.h>

namespace realm::xcvfs {
int open(const char* path, int flags, mode_t mode);
int close(int fd);
ssize_t read(int fd, void* buf, size_t n);
ssize_t write(int fd, const void* buf, size_t n);
ssize_t pread(int fd, void* buf, size_t n, off_t off);
ssize_t pwrite(int fd, const void* buf, size_t n, off_t off);
off_t lseek(int fd, off_t off, int whence);
int ftruncate(int fd, off_t size);
int posix_fallocate(int fd, off_t offset, off_t len);
int fstat(int fd, struct stat* st);
int stat(const char* path, struct stat* st);
int fsync(int fd);
int flock(int fd, int op);
void* mmap(void* addr, size_t len, int prot, int flags, int fd, off_t off);
int munmap(void* addr, size_t len);
int msync(void* addr, size_t len, int flags);
int unlink(const char* path);
} // namespace realm::xcvfs
#else
#define REALM_XCVFS 0
#endif
