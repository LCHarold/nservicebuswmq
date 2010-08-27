call "%VS90COMNTOOLS%vsvars32.bat"

md build
del build/*.* /Q
copy .\Lib\NSB\nServiceBus.* .\Build

msbuild /p:Configuration=Release

call merge_assemblies.bat /keyfile:Ttx.snk

