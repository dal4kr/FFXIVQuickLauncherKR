using System.Text;

namespace XIVLauncher.Common.Encryption;

// KR uses the plain key=value format. The global argument encryption is not part of this build.
public sealed class ArgumentBuilder
{
    private readonly List<KeyValuePair<string,string>> arguments = [];

    public ArgumentBuilder Append(string key, string value)
    {
        arguments.Add(new(key,value));
        return this;
    }

    public string Build()
    {
        var result = new StringBuilder();
        foreach (var pair in arguments)
            result.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
        return result.ToString();
    }
}
