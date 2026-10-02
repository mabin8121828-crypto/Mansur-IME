// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <utility>

namespace mansur::win {
template<class T> class ComPtr {
public:
    ComPtr() noexcept=default;
    explicit ComPtr(T* value): value_(value) { if(value_) value_->AddRef(); }
    ComPtr(const ComPtr& other):ComPtr(other.value_) {}
    ComPtr(ComPtr&& other) noexcept:value_(std::exchange(other.value_,nullptr)) {}
    ~ComPtr() { if(value_) value_->Release(); }
    ComPtr& operator=(ComPtr other) noexcept { swap(other); return *this; }
    void swap(ComPtr& other) noexcept { std::swap(value_,other.value_); }
    T* get() const noexcept {return value_;}
    T* operator->() const noexcept {return value_;}
    explicit operator bool() const noexcept {return value_!=nullptr;}
    // Only use an empty local as an output slot; never release inside a host call.
    T** put() noexcept {return &value_;}
    void attach(T* value) noexcept {ComPtr old; old.value_=std::exchange(value_,value);}
private:
    T* value_=nullptr;
};
}
