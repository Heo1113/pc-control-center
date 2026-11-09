using System.Text;
using PcControlCenter.Operator;

namespace PcControlCenter.Tests;

/// <summary>The command line of a host started with a normal-user token is built by hand; it must split back exactly.</summary>
public class WindowsCommandLineTests
{
    [Theory]
    [InlineData(@"C:\Program Files\PC Control Center\PcControlCenter.OperatorHost.exe", @"""C:\Program Files\PC Control Center\PcControlCenter.OperatorHost.exe""")]
    [InlineData("--parent-pid", "--parent-pid")]
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData("", @"""""")]
    [InlineData(@"a""b", @"""a\""b""")]
    [InlineData(@"C:\dir with space\", @"""C:\dir with space\\""")]
    [InlineData(@"a\\""b c", @"""a\\\\\""b c""")]
    public void Quotes_only_what_needs_quoting(string argument, string expected)
        => Assert.Equal(expected, WindowsCommandLine.Build([argument]));

    [Fact]
    public void Every_argument_splits_back_unchanged()
    {
        var random = new Random(20261109);
        const string alphabet = "ab \\\"\t한x";
        for (var round = 0; round < 2000; round++)
        {
            var arguments = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new string([.. Enumerable.Range(0, random.Next(0, 9)).Select(_ => alphabet[random.Next(alphabet.Length)])]))
                .ToArray();
            var line = WindowsCommandLine.Build(arguments);
            Assert.Equal(arguments, Split(line));
        }
    }

    /// <summary>The Microsoft C runtime's rules (CommandLineToArgvW for arguments after the program name).</summary>
    private static List<string> Split(string line)
    {
        var result = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }

            if (i >= line.Length)
            {
                return result;
            }

            var current = new StringBuilder();
            var quoted = false;
            while (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
            {
                var backslashes = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // "" inside quotes: a literal quote (newer runtimes); never produced by Build.
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    i++;
                }
                else
                {
                    current.Append('\\', backslashes);
                    if (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
                    {
                        current.Append(line[i]);
                        i++;
                    }
                }
            }

            result.Add(current.ToString());
        }
    }
}
