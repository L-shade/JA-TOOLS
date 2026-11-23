// =====================================================================================
//  JA para Revit — what the two tabs share.
//  The JA Famílias tab (namespaces JAEsquadrias.*) used its own copies of the common window,
//  catalog, preferences, log, icons and theme. In 2.0 the two add-ins are one DLL and those copies
//  are gone: every tool of both tabs uses the JA Tools ones (the newest of each: compact window
//  chrome, two-column options with search, tasks of the Central JA, progress box, merged catalog).
//  These global aliases make the old JA Famílias names resolve to the common types without
//  touching every file (in the JA Tools namespaces they name the very same types).
// =====================================================================================
global using OptionsDialog = RevitDwgUnifier.Tools.Common.OptionsDialog;
global using ToolCommand = RevitDwgUnifier.Tools.Common.ToolCommand;
global using ToolCancelledException = RevitDwgUnifier.Tools.Common.ToolCancelledException;
global using ResultKind = RevitDwgUnifier.Tools.Common.ResultKind;
global using ResultAction = RevitDwgUnifier.Tools.Common.ResultAction;
global using ResultDialog = RevitDwgUnifier.Tools.Common.ResultDialog;
global using ToolPrefs = RevitDwgUnifier.Tools.Common.ToolPrefs;
global using ToolCatalog = RevitDwgUnifier.Tools.Common.ToolCatalog;
global using ToolInfo = RevitDwgUnifier.Tools.Common.ToolInfo;
global using ToolChrome = RevitDwgUnifier.Tools.Common.ToolChrome;
global using ToolProgress = RevitDwgUnifier.Tools.Common.ToolProgress;
global using HelpCenter = RevitDwgUnifier.Tools.Common.HelpCenter;
global using AvisoVersao = RevitDwgUnifier.Tools.Common.AvisoVersao;
global using ParedesCebola = RevitDwgUnifier.Tools.Common.ParedesCebola;
global using IconFactory = RevitDwgUnifier.UI.IconFactory;
global using JaTheme = RevitDwgUnifier.UI.JaTheme;
global using Logger = RevitDwgUnifier.Infrastructure.Logger;
global using LogLevel = RevitDwgUnifier.Infrastructure.LogLevel;
global using AddinLocation = RevitDwgUnifier.Infrastructure.AddinLocation;
global using XlsxWriter = RevitDwgUnifier.Tools.Exporting.XlsxWriter;
global using CommandAvailability = RevitDwgUnifier.Commands.CommandAvailability;
global using AlwaysAvailable = RevitDwgUnifier.Commands.AlwaysAvailable;
global using HelpCenterCommand = RevitDwgUnifier.Commands.HelpCenterCommand;
global using TextUtil = RevitDwgUnifier.Tools.Common.TextUtil;
