using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.Validation
{
    /// <summary>
    /// UAsset 资产验证结果
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public List<ValidationWarning> Warnings { get; set; } = new List<ValidationWarning>();

        public void AddError(string category, string message, int? exportIndex = null, int? importIndex = null)
        {
            Errors.Add(new ValidationError
            {
                Category = category,
                Message = message,
                ExportIndex = exportIndex,
                ImportIndex = importIndex
            });
            IsValid = false;
        }

        public void AddWarning(string category, string message, int? exportIndex = null, int? importIndex = null)
        {
            Warnings.Add(new ValidationWarning
            {
                Category = category,
                Message = message,
                ExportIndex = exportIndex,
                ImportIndex = importIndex
            });
        }
    }

    public class ValidationError
    {
        public string Category { get; set; }
        public string Message { get; set; }
        public int? ExportIndex { get; set; }
        public int? ImportIndex { get; set; }

        public override string ToString()
        {
            var location = ExportIndex.HasValue ? $"Export[{ExportIndex}]" :
                          ImportIndex.HasValue ? $"Import[{ImportIndex}]" : "Global";
            return $"[{Category}] {location}: {Message}";
        }
    }

    public class ValidationWarning
    {
        public string Category { get; set; }
        public string Message { get; set; }
        public int? ExportIndex { get; set; }
        public int? ImportIndex { get; set; }

        public override string ToString()
        {
            var location = ExportIndex.HasValue ? $"Export[{ExportIndex}]" :
                          ImportIndex.HasValue ? $"Import[{ImportIndex}]" : "Global";
            return $"[{Category}] {location}: {Message}";
        }
    }

    /// <summary>
    /// UAsset 资产验证器
    /// </summary>
    public class UAssetValidator
    {
        private readonly string _gameContentPath;
        private readonly HashSet<string> _existingAssets;

        /// <summary>
        /// 创建验证器实例
        /// </summary>
        /// <param name="gameContentPath">游戏 Content 目录路径，用于验证导入资产是否存在</param>
        public UAssetValidator(string gameContentPath = null)
        {
            _gameContentPath = gameContentPath;
            _existingAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(gameContentPath) && Directory.Exists(gameContentPath))
            {
                PreloadAssetRegistry(gameContentPath);
            }
        }

        /// <summary>
        /// 预加载游戏资产目录，建立资产索引
        /// </summary>
        private void PreloadAssetRegistry(string contentPath)
        {
            try
            {
                var uassetFiles = Directory.GetFiles(contentPath, "*.uasset", SearchOption.AllDirectories);
                foreach (var file in uassetFiles)
                {
                    // 转换为游戏内路径格式：/Game/Path/To/Asset
                    var relativePath = file.Substring(contentPath.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(".uasset", "")
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');

                    // 处理 FSD/Content 前缀
                    if (relativePath.StartsWith("FSD/Content/", StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = "/Game/" + relativePath.Substring("FSD/Content/".Length);
                    }
                    else if (!relativePath.StartsWith("/Game/"))
                    {
                        relativePath = "/Game/" + relativePath;
                    }

                    _existingAssets.Add(relativePath);
                }
            }
            catch (Exception)
            {
                // 忽略预加载错误
            }
        }

        /// <summary>
        /// 验证资产
        /// </summary>
        public ValidationResult Validate(UAsset asset)
        {
            var result = new ValidationResult();

            // 1. 验证所有 FPackageIndex 指向有效索引
            ValidatePackageIndices(asset, result);

            // 2. 验证导入路径指向实际存在的资产
            ValidateImportPaths(asset, result);

            // 3. 验证类引用一致性
            ValidateClassReferences(asset, result);

            return result;
        }

        /// <summary>
        /// 验证所有 FPackageIndex 指向有效索引
        /// </summary>
        private void ValidatePackageIndices(UAsset asset, ValidationResult result)
        {
            int importCount = asset.Imports.Count;
            int exportCount = asset.Exports.Count;

            for (int i = 0; i < exportCount; i++)
            {
                var export = asset.Exports[i];

                // 验证 ClassIndex
                ValidateIndex(asset, export.ClassIndex, "ClassIndex", i, result, importCount, exportCount);

                // 验证 SuperIndex
                ValidateIndex(asset, export.SuperIndex, "SuperIndex", i, result, importCount, exportCount);

                // 验证 TemplateIndex
                ValidateIndex(asset, export.TemplateIndex, "TemplateIndex", i, result, importCount, exportCount);

                // 验证 OuterIndex
                ValidateIndex(asset, export.OuterIndex, "OuterIndex", i, result, importCount, exportCount);

                // 特殊处理 ClassExport 和 StructExport
                if (export is ClassExport classExport)
                {
                    ValidateIndex(asset, classExport.ClassWithin, "ClassWithin", i, result, importCount, exportCount);
                }

                if (export is StructExport structExport)
                {
                    ValidateIndex(asset, structExport.SuperStruct, "SuperStruct", i, result, importCount, exportCount);
                }
            }
        }

        /// <summary>
        /// 验证单个索引是否有效
        /// </summary>
        private void ValidateIndex(UAsset asset, FPackageIndex index, string indexName, int exportIndex,
            ValidationResult result, int importCount, int exportCount)
        {
            if (index.IsNull())
                return;

            if (index.IsImport())
            {
                // 导入索引：-1 到 -importCount 是有效的
                int importIdx = -index.Index - 1;
                if (importIdx < 0 || importIdx >= importCount)
                {
                    result.AddError("InvalidIndex",
                        $"{indexName} points to invalid import index {index.Index} (valid range: -1 to -{importCount})",
                        exportIndex);
                }
            }
            else if (index.IsExport())
            {
                // 导出索引：1 到 exportCount 是有效的（注意：UAssetAPI 中导出索引从1开始）
                if (index.Index < 1 || index.Index > exportCount)
                {
                    result.AddError("InvalidIndex",
                        $"{indexName} points to invalid export index {index.Index} (valid range: 1 to {exportCount})",
                        exportIndex);
                }
            }
        }

        /// <summary>
        /// 验证导入路径指向实际存在的资产
        /// </summary>
        private void ValidateImportPaths(UAsset asset, ValidationResult result)
        {
            if (string.IsNullOrEmpty(_gameContentPath) || _existingAssets.Count == 0)
            {
                result.AddWarning("ImportValidation", "Game content path not provided, skipping import path validation");
                return;
            }

            for (int i = 0; i < asset.Imports.Count; i++)
            {
                var import = asset.Imports[i];

                // 构建完整路径
                string fullPath = BuildImportPath(asset, import);

                if (string.IsNullOrEmpty(fullPath))
                    continue;

                // 检查是否是 /Script/ 路径（引擎内置类，不需要验证文件存在）
                if (fullPath.StartsWith("/Script/"))
                    continue;

                // 检查资产是否存在
                if (!_existingAssets.Contains(fullPath))
                {
                    result.AddError("MissingImport",
                        $"Import '{fullPath}' not found in game assets",
                        importIndex: i);
                }
            }
        }

        /// <summary>
        /// 构建导入的完整路径
        /// </summary>
        private string BuildImportPath(UAsset asset, Import import)
        {
            if (import?.ClassName == null)
                return null;

            // 获取对象名
            string objectName = import.ObjectName?.ToString();
            if (string.IsNullOrEmpty(objectName))
                return null;

            // 获取包名（从 OuterIndex 链构建）
            string packagePath = GetPackagePath(asset, import);

            if (string.IsNullOrEmpty(packagePath))
                return null;

            return $"{packagePath}.{objectName}";
        }

        /// <summary>
        /// 获取导入的包路径
        /// </summary>
        private string GetPackagePath(UAsset asset, Import import)
        {
            // 如果是顶层导入（OuterIndex 为 null），使用 ClassPackage
            if (import.OuterIndex.IsNull())
            {
                return import.ClassPackage?.ToString();
            }

            // 否则递归构建路径
            var parts = new List<string>();
            var current = import;

            while (current != null && !current.OuterIndex.IsNull())
            {
                parts.Insert(0, current.ObjectName?.ToString());

                if (current.OuterIndex.IsImport())
                {
                    int parentIdx = -current.OuterIndex.Index - 1;
                    if (parentIdx >= 0 && parentIdx < asset.Imports.Count)
                    {
                        current = asset.Imports[parentIdx];
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            // 添加最外层的包名
            if (current != null && !string.IsNullOrEmpty(current.ClassPackage?.ToString()))
            {
                string basePath = current.ClassPackage.ToString();
                if (parts.Count > 0)
                {
                    return $"{basePath}/{string.Join("/", parts)}";
                }
                return basePath;
            }

            return null;
        }

        /// <summary>
        /// 验证类引用一致性
        /// </summary>
        private void ValidateClassReferences(UAsset asset, ValidationResult result)
        {
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var export = asset.Exports[i];

                // 检查 ClassIndex 是否为 null（除了顶级包导出）
                if (export.ClassIndex.IsNull() && export.OuterIndex.IsNull())
                {
                    // 顶级包导出允许 ClassIndex 为 null
                    continue;
                }

                if (export.ClassIndex.IsNull())
                {
                    result.AddWarning("NullClass",
                        $"Export '{export.ObjectName}' has null ClassIndex",
                        i);
                }
            }
        }

        /// <summary>
        /// 快速验证资产文件
        /// </summary>
        public static ValidationResult QuickValidate(string assetPath, EngineVersion version, string gameContentPath = null)
        {
            var validator = new UAssetValidator(gameContentPath);

            try
            {
                var asset = new UAsset(assetPath, version);
                return validator.Validate(asset);
            }
            catch (Exception ex)
            {
                var result = new ValidationResult();
                result.AddError("LoadError", $"Failed to load asset: {ex.Message}");
                return result;
            }
        }
    }
}
