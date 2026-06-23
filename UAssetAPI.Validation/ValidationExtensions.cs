using System;
using System.Linq;

namespace UAssetAPI.Validation
{
    /// <summary>
    /// UAsset 验证扩展方法
    /// </summary>
    public static class ValidationExtensions
    {
        /// <summary>
        /// 验证资产并打印结果
        /// </summary>
        public static bool ValidateAndPrint(this UAsset asset, string gameContentPath = null)
        {
            var validator = new UAssetValidator(gameContentPath);
            var result = validator.Validate(asset);

            PrintResult(result);
            return result.IsValid;
        }

        /// <summary>
        /// 打印验证结果
        /// </summary>
        public static void PrintResult(ValidationResult result)
        {
            Console.WriteLine("======================================");
            Console.WriteLine($"Validation Result: {(result.IsValid ? "PASSED" : "FAILED")}");
            Console.WriteLine("======================================");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine($"\nErrors ({result.Errors.Count}):");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"  ❌ {error}");
                }
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine($"\nWarnings ({result.Warnings.Count}):");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"  ⚠️  {warning}");
                }
            }

            if (result.IsValid && result.Warnings.Count == 0)
            {
                Console.WriteLine("\n✅ Asset validation passed with no issues!");
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 获取验证结果的摘要
        /// </summary>
        public static string GetSummary(this ValidationResult result)
        {
            if (result.IsValid && result.Warnings.Count == 0)
                return "Valid";

            if (result.IsValid)
                return $"Valid with {result.Warnings.Count} warnings";

            return $"Invalid: {result.Errors.Count} errors, {result.Warnings.Count} warnings";
        }

        /// <summary>
        /// 检查是否有特定类别的错误
        /// </summary>
        public static bool HasErrorCategory(this ValidationResult result, string category)
        {
            return result.Errors.Any(e => e.Category == category);
        }

        /// <summary>
        /// 检查是否有无效索引错误
        /// </summary>
        public static bool HasInvalidIndexErrors(this ValidationResult result)
        {
            return result.HasErrorCategory("InvalidIndex");
        }

        /// <summary>
        /// 检查是否有缺失导入错误
        /// </summary>
        public static bool HasMissingImportErrors(this ValidationResult result)
        {
            return result.HasErrorCategory("MissingImport");
        }
    }
}
