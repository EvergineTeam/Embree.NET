// Minimal <stddef.h> stub.
//
// The generator parses the headers hermetically: the stubs folder is the first -I
// entry, so these definitions win over whatever libc/MSVC headers happen to exist on
// the machine running the generator. That is what keeps the generated bindings byte
// for byte identical between a Windows dev box and the linux-x64 CI runner.
//
// Sizes match the pinned x86_64 target used in Program.cs.

#pragma once

typedef unsigned long long size_t;
typedef long long ptrdiff_t;

#define NULL ((void*)0)
#define offsetof(type, member) __builtin_offsetof(type, member)
