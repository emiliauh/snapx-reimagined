using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using SnapX.Core.Utils.Extensions;
using YamlDotNet.Serialization;

namespace SnapX.Core.Indexer;

public class IndexerSettings
{
    [Category("Indexer"), DefaultValue(IndexerOutput.Html), Description("Select the index output type.")]
    public IndexerOutput Output { get; set; }

    [Category("Indexer"), DefaultValue(true), Description("Do not include hidden folders in the index.")]
    public bool SkipHiddenFolders { get; set; }

    [Category("Indexer"), DefaultValue(true), Description("Do not include hidden files in the index.")]
    public bool SkipHiddenFiles { get; set; }

    [Category("Indexer"), DefaultValue(0), Description("Set the maximum folder depth. Enter 0 for no limit.")]
    public int MaxDepthLevel { get; set; }

    [Category("Indexer"), DefaultValue(true), Description("Show folder and file sizes.")]
    public bool ShowSizeInfo { get; set; }

    [Category("Indexer"), DefaultValue(true), Description("Show the application name and generation time in the footer.")]
    public bool AddFooter { get; set; }

    [Category("Text index"), DefaultValue("|___"), Description("Set the text that identifies each folder level.")]
    public string IndentationText { get; set; }

    [Category("Text index"), DefaultValue(false), Description("Add an empty line after each folder.")]
    public bool AddEmptyLineAfterFolders { get; set; }

    [Category("HTML index"), DefaultValue(false), Description("Use a custom CSS file.")]
    public bool UseCustomCSSFile { get; set; }

    [Category("HTML index"), DefaultValue(false), Description("Show the path of each subfolder.")]
    public bool DisplayPath { get; set; }

    [Category("Indexer / HTML"), DefaultValue(false), Description("Limit the display path to the selected root folder. Must have DisplayPath enabled.")]
    public bool DisplayPathLimited { get; set; }

    [Category("Indexer / HTML"), DefaultValue(""), Description("Custom Cascading Style Sheet file path.")]
    public string CustomCSSFilePath { get; set; }

    [Category("Indexer / XML"), DefaultValue(true), Description("Folder/File information (name, size etc.) will be written as attribute.")]
    public bool UseAttribute { get; set; }

    [Category("Indexer / JSON"), DefaultValue(true), Description("Creates parseable but longer json output.")]
    public bool CreateParseableJson { get; set; }

    [JsonIgnore, YamlIgnore]
    public bool BinaryUnits;

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public IndexerSettings()
    {
        this.ApplyDefaultPropertyValues();
    }
}
