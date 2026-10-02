// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <atomic>
namespace mansur::win {
extern HINSTANCE module_instance;
extern std::atomic<long> live_objects;
extern std::atomic<long> server_locks;
struct ModuleObject {
    ModuleObject() noexcept {++live_objects;}
    ~ModuleObject() {--live_objects;}
};
}
