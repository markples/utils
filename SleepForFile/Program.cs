using System;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Reflection;

// Started as general utility, then specialized to mt.exe, probably mixed up...

int argIndex = 0;
string? filename = null;
string? program = null;
string[] programArguments = Array.Empty<string>();
int minMillis = 0;
int maxMillis = 5000;

DateTime start = DateTime.Now;
bool done = false;
while (!done && (argIndex < args.Length))
{
    string arg = args[argIndex];
    switch (arg)
    {
        case "-n":
        case "--min":
            minMillis = IntArgValue(args, ref argIndex);
            break;

        case "-x":
        case "--max":
            maxMillis = IntArgValue(args, ref argIndex);
            break;

        case "-p":
        case "--program":
            program = StringArgValue(args, ref argIndex);
            break;

        case "-f":
        case "--filename":
            filename = StringArgValue(args, ref argIndex);
            break;

        default:
            done = true;
            break;
    }
}

if (argIndex < args.Length)
{
    programArguments = args[argIndex..];
}

bool isMT = false;
if (program == null)
{
    string executable = Environment.ProcessPath!;
    if (Path.GetFileName(executable) != "mt.exe")
    {
        throw new Exception("Expected to be \"mt.exe\"");
    }
    string? dirName = Path.GetDirectoryName(executable);
    program = Path.Combine(dirName!, "mt_.exe");
    isMT = true;
}

if (isMT && (filename == null))
{
    foreach (string programArgument in programArguments)
    {
        Console.WriteLine(programArgument);
        string lower = programArgument.ToLower();
        if (lower.StartsWith("/outputresource:") || lower.StartsWith("-outputresource:"))
        {
            filename = lower["/outputresource:".Length..];
            if (filename.Contains(';'))
            {
                filename = filename[0..filename.IndexOf(';')];
            }
            break;
        }
    }
}

//if (filename == null)
//{
//    Usage();
//    throw new Exception("No file to wait on");
//}

bool hasSeenFileLocked = false;

while ((DateTime.Now - start).TotalMilliseconds < maxMillis)
{
    try
    {
        if (filename != null)
        {
            using (File.Open(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        if (hasSeenFileLocked || ((DateTime.Now - start).TotalMilliseconds >= minMillis)) break; // done!
    }
    catch (FileNotFoundException) { throw; }
    catch (PathTooLongException) { throw; }
    catch (DirectoryNotFoundException) { throw; }
    catch (IOException)
    {
        // is this right?
        hasSeenFileLocked = true;
    }

    // sleep?
}

ProcessStartInfo psi = new()
{
    FileName = program,
    Arguments = string.Join(' ', programArguments), // quoting? does this work?
    UseShellExecute = false
};

var process = Process.Start(psi);
process!.WaitForExit();

string ArgValue(string[] args, int argIndex)
{
    if (argIndex + 1 >= args.Length)
    {
        Usage();
        throw new Exception($"Argument value needed for {args[argIndex]}");
    }

    string value = args[argIndex + 1];
    return value;
}

string StringArgValue(string[] args, ref int argIndex)
{
    string value = ArgValue(args, argIndex);
    argIndex += 2;
    return value;
}

int IntArgValue(string[] args, ref int argIndex)
{
    string stringValue = ArgValue(args, argIndex);
    if (!Int32.TryParse(stringValue, out int value))
    {
        Usage();
        throw new Exception($"Integer argument value needed for {args[argIndex]}");
    }

    argIndex += 2;
    return value;
}

void Usage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("    SleepForFile [-n | --min <ms>] [-x | --max <ms>] [-p | --program <program>] [-f | --filename <file>] [args...]");
    Console.WriteLine("or  mt.exe <mt arguments>");
}
