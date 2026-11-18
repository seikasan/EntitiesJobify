using System;
using System.IO;
using System.Security;
using UnityEditor;
using UnityEngine;

namespace EntitiesJobify
{
    internal sealed class RiderCodeFixProjectReference : AssetPostprocessor
    {
        private const string CodeFixGuid = "d9964cf61452ae1468282f4b3002e738";

        public static string OnGeneratedCSProject(string path, string content)
        {
            var codeFixAssetPath = AssetDatabase.GUIDToAssetPath(CodeFixGuid);
            if (string.IsNullOrEmpty(codeFixAssetPath))
            {
                return content;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var codeFixPath = Path.GetFullPath(Path.Combine(projectRoot, codeFixAssetPath));
            if (content.IndexOf(codeFixPath, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return content;
            }

            var projectClosingTagIndex = content.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
            if (projectClosingTagIndex < 0)
            {
                return content;
            }

            var escapedCodeFixPath = SecurityElement.Escape(codeFixPath);
            var itemGroup =
                "    <ItemGroup>" + Environment.NewLine +
                "        <Analyzer Include=\"" + escapedCodeFixPath + "\" />" + Environment.NewLine +
                "    </ItemGroup>" + Environment.NewLine;

            return content.Insert(projectClosingTagIndex, itemGroup);
        }
    }
}
