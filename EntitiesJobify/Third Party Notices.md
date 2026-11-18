# Third Party Notices

The Rider code fix ships the following third-party assemblies and native libraries under the `Editor/Analyzers/Rider` directory:

- **Microsoft.CodeAnalysis**, **Microsoft.CodeAnalysis.CSharp**, **Microsoft.CodeAnalysis.Workspaces**, and **Microsoft.CodeAnalysis.CSharp.Workspaces** — Microsoft Corporation, MIT License. License: <https://github.com/dotnet/roslyn/blob/main/License.txt>.
- **Microsoft.Bcl.AsyncInterfaces**, **System.Composition.AttributedModel**, **System.Composition.Hosting**, **System.Composition.Runtime**, and **System.Composition.TypedParts** — Microsoft Corporation, MIT License. License: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.
- **System.Threading.Channels** — .NET Foundation, MIT License. License: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.
- **Humanizer** — Humanizer contributors, MIT License. License: <https://github.com/Humanizr/Humanizer/blob/main/LICENSE>.
- **SQLitePCLRaw.batteries_v2**, **SQLitePCLRaw.core**, and **SQLitePCLRaw.provider.e_sqlite3** — SourceGear, Apache License 2.0. License: <https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT>.
- **e_sqlite3 native libraries** — SQLite, public domain. License: <https://sqlite.org/copyright.html>.

These dependencies support Rider's analyzer/code fix host. Their Unity PluginImporter settings exclude them from Unity Editor and Player plugin references.