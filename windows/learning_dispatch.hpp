// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "mansur/core.hpp"
#include <windows.h>
#include <cstdint>
#include <string>

namespace mansur::win {
struct CaretLocation {RECT rect{}; bool valid=false; HWND owner=nullptr;};
struct LearningRequest {
    const Commit& commit;
    std::uint64_t context_id=0;
    std::uint64_t source_revision=0;
    CaretLocation caret;
    HWND writeback_endpoint=nullptr;
    std::string writeback_token;
};
enum class DispatchResult { Queued, NotConnected, Rejected };
enum class DeliveryStatus { Idle, Queued, Delivered, Unavailable, TimedOut, Rejected };
struct DeliverySnapshot {DeliveryStatus status=DeliveryStatus::Idle;std::uint64_t context_id=0;};
// Called only after a confirmed text write. Must never wait for a model/network
// or retain the borrowed request/commit. A future transport must copy into a
// bounded local queue and make newer requests invalidate older results.
DispatchResult LearningDispatch(const LearningRequest& request) noexcept;
DispatchResult CancelLearning(std::uint64_t context_id) noexcept;
DeliverySnapshot GetLastDeliveryStatus() noexcept;
#ifdef MANSUR_TRANSPORT_TEST
std::wstring LearningTestPipeName();
void LearningTestFailCounter(bool fail) noexcept;
#endif
}
