// .NET 11's runtime packs are compiled for macOS 14, so pal_swiftbindings.o (CryptoKit) imports the two "…2" Swift
// metadata lookups, which macOS 13's libswiftCore does not export: dyld aborts the process before main (issue #27).
// Defining them here keeps them out of the binary's imports. The older entry points exist on every macOS and run the
// same implementation; the "2" versions only authenticate `context` first, which is a no-op off arm64e, and the release
// RIDs are arm64 and x86_64 (swift stdlib/public/runtime/MetadataLookup.cpp). Same swiftcc, so a plain forward.

#define SWIFT_ENTRY __attribute__((swiftcall))
#define LOCAL_ENTRY __attribute__((swiftcall, visibility("hidden")))

SWIFT_ENTRY extern const void *swift_getTypeByMangledNameInContext(
    const char *name, __SIZE_TYPE__ length, const void *context, const void *const *args);

SWIFT_ENTRY extern const void *swift_getTypeByMangledNameInContextInMetadataState(
    __SIZE_TYPE__ state, const char *name, __SIZE_TYPE__ length, const void *context, const void *const *args);

LOCAL_ENTRY const void *swift_getTypeByMangledNameInContext2(
    const char *name, __SIZE_TYPE__ length, const void *context, const void *const *args) {
  return swift_getTypeByMangledNameInContext(name, length, context, args);
}

LOCAL_ENTRY const void *swift_getTypeByMangledNameInContextInMetadataState2(
    __SIZE_TYPE__ state, const char *name, __SIZE_TYPE__ length, const void *context, const void *const *args) {
  return swift_getTypeByMangledNameInContextInMetadataState(state, name, length, context, args);
}
