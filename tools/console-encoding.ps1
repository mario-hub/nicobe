# Dot-source in build/release scripts: .NET CLI emits UTF-8 even on Windows PowerShell 5.1.
$OutputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::InputEncoding=$OutputEncoding
[Console]::OutputEncoding=$OutputEncoding
# PowerShell 5.1 uses Out-File for redirection; PowerShell 7 also accepts this default.
$PSDefaultParameterValues=@{}+$PSDefaultParameterValues
$PSDefaultParameterValues['Out-File:Encoding']='utf8'
