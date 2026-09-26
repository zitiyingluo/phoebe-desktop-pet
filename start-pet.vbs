' start-pet.vbs - launch the desktop pet with NO console window at all.
'
' Why this file exists:
'   The pet is a WinForms app hosted by dotnet.exe, which is a CONSOLE-subsystem
'   program. Starting it from a .cmd always drags a console along: "start" opens
'   one, and even PowerShell's -WindowStyle Hidden still creates a console that
'   merely stays invisible. wscript.exe is a GUI-subsystem host, so a process it
'   spawns inherits no console whatsoever - nothing to flash, nothing to hide.
'
' Keep this file ASCII-only: wscript reads it with the system ANSI codepage.

Option Explicit

Dim fso, shell, here, dotnet, dll
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

here = fso.GetParentFolderName(WScript.ScriptFullName)
dotnet = "C:\Program Files\dotnet\dotnet.exe"
dll = here & "\PhoebePet.dll"

If Not fso.FileExists(dotnet) Then
    MsgBox "dotnet not found at " & dotnet & vbCrLf & _
           "Please install the .NET 8 or newer Desktop Runtime.", _
           16, "Desktop Pet"
    WScript.Quit 1
End If

If Not fso.FileExists(dll) Then
    MsgBox "PhoebePet.dll not found." & vbCrLf & _
           "Run build.cmd first to compile the pet.", _
           16, "Desktop Pet"
    WScript.Quit 1
End If

' 0 = hidden window, False = do not wait for the child to exit.
shell.CurrentDirectory = here
shell.Run """" & dotnet & """ """ & dll & """", 0, False
