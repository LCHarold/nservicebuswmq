call "%VS90COMNTOOLS%vsvars32.bat"

md build
del build/*.* /Q

copy .\Lib\NSB\nServiceBus.* .\Build

msbuild

merge_assemblies.bat


pause
