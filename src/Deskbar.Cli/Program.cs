using System.Diagnostics;
using System.Text;
using Deskbar.Core.Control;
using Deskbar.Windows.Control;

// Entry point of deskbar-cli.exe, the console client behind the Store package's `deskbar` alias.
// All the behaviour is in ControlConsole; this only supplies the real pipe name, the real asker and
// the real streams.

// UTF-8 without a BOM, as the deskbar.cmd shim path produces: a failure message can carry a workspace
// name in any script, and a BOM would corrupt the first line of JSON an agent parses.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = utf8;

// The pipe of THIS login session's Deskbar, the same name ControlCommandLine uses for `ctl`.
var pipeName = ControlWire.PipeName(Process.GetCurrentProcess().SessionId);

return ControlConsole.Run(args, pipeName, ControlClient.RunAsync, Console.Out, Console.Error);
