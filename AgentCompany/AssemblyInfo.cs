using System.Windows;

// 单测访问 internal（看门狗判定 EvaluateWatchdog 等，B1 修复配套）
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentCompany.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
