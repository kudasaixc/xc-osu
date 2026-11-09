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

#if defined(__EMSCRIPTEN_WASM_WORKERS__)
#error "This ExternalCommitHelper implementation is not compatible with wasm workers."
#endif

#include <realm/object-store/impl/external_commit_helper.hpp>
#include <realm/object-store/impl/realm_coordinator.hpp>

using namespace realm;
using namespace realm::_impl;

#if defined(__EMSCRIPTEN_PTHREADS__)
ExternalCommitHelper::ExternalCommitHelper(RealmCoordinator& parent, const RealmConfig&)
    : m_parent(parent)
{
    m_thread = std::thread([this] {
        std::unique_lock<std::mutex> lock(m_mutex);
        while (true) {
            m_cv.wait(lock, [this] { return m_pending || m_stop; });
            if (m_stop)
                return;
            m_pending = false;
            lock.unlock();
            m_parent.on_change();
            lock.lock();
        }
    });
}

ExternalCommitHelper::~ExternalCommitHelper()
{
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_stop = true;
    }
    m_cv.notify_one();
    if (m_thread.joinable())
        m_thread.join();
}

void ExternalCommitHelper::notify_others()
{
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_pending = true;
    }
    m_cv.notify_one();
}
#else
ExternalCommitHelper::ExternalCommitHelper(RealmCoordinator& parent, const RealmConfig&)
    : m_parent(parent)
{
}

ExternalCommitHelper::~ExternalCommitHelper() = default;

void ExternalCommitHelper::notify_others()
{
    m_parent.on_change();
}
#endif
