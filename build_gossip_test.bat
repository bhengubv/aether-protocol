@echo off
setlocal

set CL_EXE=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\cl.exe
set VC_INC=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\include
set VC_LIB=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\lib\x64
set SDK_ROOT=C:\Program Files (x86)\Windows Kits\10
set SDK_VER=10.0.26100.0
set UCRT_INC=%SDK_ROOT%\Include\%SDK_VER%\ucrt
set UM_INC=%SDK_ROOT%\Include\%SDK_VER%\um
set SHARED_INC=%SDK_ROOT%\Include\%SDK_VER%\shared
set UCRT_LIB=%SDK_ROOT%\Lib\%SDK_VER%\ucrt\x64
set UM_LIB=%SDK_ROOT%\Lib\%SDK_VER%\um\x64

cd /d "C:\Dev\Solutions\com.bhengubv\aether-protocol"

"%CL_EXE%" /W4 /WX /nologo ^
    /I "c\include" ^
    /I "%VC_INC%" ^
    /I "%UCRT_INC%" ^
    /I "%UM_INC%" ^
    /I "%SHARED_INC%" ^
    c\src\aether_reputation.c ^
    c\src\aether_gossip.c ^
    c\tests\test_gossip.c ^
    /Fe:test_gossip.exe ^
    /link ^
    /LIBPATH:"%VC_LIB%" ^
    /LIBPATH:"%UCRT_LIB%" ^
    /LIBPATH:"%UM_LIB%"

exit /b %ERRORLEVEL%
