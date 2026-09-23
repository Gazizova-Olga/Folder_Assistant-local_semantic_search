namespace FolderAssistant.Surfaces;

/// <summary>
/// Where the console front end reads and writes, and whether the input is a person. The composition
/// root hands it the process's own standard input and output; a test hands it a scripted reader and a
/// writer it can read back, which is how the loop and the host's response to <c>exit</c> are checked
/// without a terminal.
/// </summary>
/// <param name="InputRedirected">
/// True when standard input is not an interactive console — a pipe, a file, a service host, a container
/// with no terminal. The loop is not started over a redirected input: it would read to the end at once and
/// nobody is there to type, and the host keeps serving until it is stopped.
/// </param>
internal sealed record ConsoleStreams(TextReader Input, TextWriter Output, Boolean InputRedirected);
