////////////////////////////////////////////////////////////////////////////
//
// Copyright 2023 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

#include <condition_variable>
#include <mutex>
#include <thread>

namespace realm {
struct RealmConfig;

namespace _impl {
class RealmCoordinator;

class ExternalCommitHelper {
public:
    ExternalCommitHelper(RealmCoordinator& parent, const RealmConfig&);
    ~ExternalCommitHelper();

    void notify_others();

private:
    RealmCoordinator& m_parent;
#if defined(__EMSCRIPTEN_PTHREADS__)
    // xc-osu!: all realms live in one wasm process, so "other processes" are only other threads.
    // Like the epoll helper, change notification runs on a dedicated daemon thread.
    std::mutex m_mutex;
    std::condition_variable m_cv;
    bool m_pending = false;
    bool m_stop = false;
    std::thread m_thread;
#endif
};

} // namespace _impl
} // namespace realm
