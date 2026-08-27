
// SPDX-License-Identifier: GPL-3.0-or-later


namespace SnapX.Core.Upload.Custom.Functions;

// Example: {outputbox:text}
// Example: {outputbox:title|text}
internal class CustomUploaderFunctionOutputBox : CustomUploaderFunction
{
    public override string Name { get; } = "outputbox";

    public override int MinParameterCount { get; } = 1;

    public override string? Call(ShareXCustomUploaderSyntaxParser parser, string?[] parameters)
    {
        string? text;
        string? title = null;

        if (parameters is { Length: > 1 })
        {
            title = parameters[0];
            text = parameters[1];
        }
        else
        {
            text = parameters is { Length: > 0 } ? parameters[0] : null;
        }

        if (!string.IsNullOrEmpty(text))
        {
            if (string.IsNullOrEmpty(title))
            {
                title = "Output";
            }
            parser.Interaction.ShowOutput(title, text);
        }

        return null;
    }
}
