@echo off
rem Compiles HdrToneMapping.hlsl to the embedded bytecode. Needs fxc.exe from the Windows SDK (on the PATH, or set FXC to its path).
rem The flags must match the ones in HdrToneMappingTests, which checks the embedded bytecode against the source.

setlocal
cd /d "%~dp0"
if not defined FXC set FXC=fxc.exe

"%FXC%" /nologo /T vs_4_0 /E VSMain /O3 /Qstrip_debug /Qstrip_reflect /Fo HdrToneMapping.vs.cso HdrToneMapping.hlsl || exit /b 1
"%FXC%" /nologo /T ps_4_0 /E PSMain /O3 /Qstrip_debug /Qstrip_reflect /Fo HdrToneMapping.ps.cso HdrToneMapping.hlsl || exit /b 1
