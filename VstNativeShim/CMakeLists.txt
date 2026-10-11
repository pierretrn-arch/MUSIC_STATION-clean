cmake_minimum_required(VERSION 3.20)
project(VstNativeShim CXX)

# Standalone throwaway prototype per PHASE4-VST3-NATIVE.md - NOT part of ToneDetector.sln. x64
# only (matches Surge XT's own x86_64-win native module).

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(SDK_ROOT "${CMAKE_SOURCE_DIR}/vst3sdk")

# Shared by both the Milestone 1-3 console harness and the Milestone 4 flat-C-API DLL, so the
# SDK source list (discovered by tracing #includes across milestones 1-3, see PROGRESS-NATIVE.md)
# only has to be maintained in one place.
add_library(VstSdkHosting STATIC
    "${SDK_ROOT}/public.sdk/source/vst/hosting/module.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/module_win32.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/hostclasses.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/processdata.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/eventlist.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/connectionproxy.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/hosting/parameterchanges.cpp"
    "${SDK_ROOT}/public.sdk/source/common/threadchecker_win32.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/utility/stringconvert.cpp"
    "${SDK_ROOT}/public.sdk/source/common/commonstringconvert.cpp"
    "${SDK_ROOT}/public.sdk/source/vst/vstinitiids.cpp"
    "${SDK_ROOT}/public.sdk/source/common/commoniids.cpp"
    "${SDK_ROOT}/public.sdk/source/common/memorystream.cpp"
    "${SDK_ROOT}/pluginterfaces/base/funknown.cpp"
    "${SDK_ROOT}/pluginterfaces/base/coreiids.cpp"
    "${SDK_ROOT}/pluginterfaces/base/conststringtable.cpp"
    "${SDK_ROOT}/pluginterfaces/base/ustring.cpp"
)
target_include_directories(VstSdkHosting PUBLIC "${SDK_ROOT}")
if(MSVC)
    target_compile_definitions(VstSdkHosting PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

# Milestones 1-3: console test harness.
add_executable(VstNativeShim src/main.cpp)
target_link_libraries(VstNativeShim PRIVATE VstSdkHosting)
if(MSVC)
    target_compile_definitions(VstNativeShim PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

# Milestone 4: flat extern "C" API, P/Invoked from the C# round-trip test project
# (CSharpRoundTripTest/, itself not part of ToneDetector.sln).
add_library(VstShim SHARED shim/vstshim.cpp)
target_link_libraries(VstShim PRIVATE VstSdkHosting)
if(MSVC)
    target_compile_definitions(VstShim PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
