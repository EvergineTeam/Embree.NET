// Stub for <sys/types.h>.
//
// rtcore_common.h includes <sys/types.h>, which does not exist for the pinned
// x86_64-pc-windows clang target used by the generator (and would otherwise make
// the generated bindings depend on whichever libc headers the build machine has).
// Embree only needs ssize_t from it, and rtcore_common.h defines that itself under
// _WIN32, so an empty stub is enough and keeps the generator output deterministic.

#pragma once
