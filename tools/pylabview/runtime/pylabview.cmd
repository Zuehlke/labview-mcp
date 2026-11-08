@echo off
rem Launcher for the bundled pylabview. Resolves its own location, so the caller needs to know
rem nothing about the layout and nothing has to be on PATH or installed.
rem
rem   pylabview.cmd -x -i "C:\path\My.vi" -m "C:\out\My.xml"     binary -> XML
rem   pylabview.cmd -c -m "C:\out\My.xml" -i "C:\out\My.vi"      XML -> binary
rem   pylabview.cmd --version
rem
rem A pythonNNN._pth beside python.exe keeps this isolated: no registry, no PYTHONPATH,
rem no site-packages. Provisioned by provision.ps1.
setlocal
set "HERE=%~dp0"
"%HERE%python.exe" "%HERE%app\pylabview\readRSRC.py" %*
exit /b %errorlevel%
