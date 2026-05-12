@echo off
setlocal

set CL_EXE=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133\bin\Hostx64\x64\cl.exe
set VC_INC=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133\include
set VC_LIB=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133\lib\x64
set SDK_ROOT=C:\Program Files (x86)\Windows Kits\10
set SDK_VER=10.0.26100.0
set UCRT_INC=%SDK_ROOT%\Include\%SDK_VER%\ucrt
set UM_INC=%SDK_ROOT%\Include\%SDK_VER%\um
set SHARED_INC=%SDK_ROOT%\Include\%SDK_VER%\shared
set UCRT_LIB=%SDK_ROOT%\Lib\%SDK_VER%\ucrt\x64
set UM_LIB=%SDK_ROOT%\Lib\%SDK_VER%\um\x64

set REPO=C:\Dev\Solutions\com.bhengubv\aether-protocol

cd /d "%REPO%"

"%CL_EXE%" /W4 /nologo /std:c11 ^
    /I "c\include" ^
    /I "%VC_INC%" ^
    /I "%UCRT_INC%" ^
    /I "%UM_INC%" ^
    /I "%SHARED_INC%" ^
    /D "_CRT_SECURE_NO_WARNINGS" ^
    /D "AETHER_NO_SODIUM" ^
    /wd4996 ^
    c\src\protocol.c ^
    c\src\routing.c ^
    c\src\aether_reputation.c ^
    c\src\security_win_stub.c ^
    c\tests\test_routing.c ^
    /Fe:test_routing.exe ^
    /link ^
    /LIBPATH:"%VC_LIB%" ^
    /LIBPATH:"%UCRT_LIB%" ^
    /LIBPATH:"%UM_LIB%"

exit /b %ERRORLEVEL%
