// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See NuGet.Client.LICENSE.txt.
// Adapted from NuGet.Client: namespace and resource strings changed; unused overload omitted.
// https://github.com/NuGet/NuGet.Client/blob/977537e19c6be57fead1411e6cf05f936bf1baf4/src/NuGet.Core/NuGet.Packaging/PackageCreation/Utility/PackageIdValidator.cs

using System.Globalization;
using System.Text.RegularExpressions;

namespace DotnetPackageSkills.NuGet;

internal static class PackageIdValidator
{
    public const int MaxPackageIdLength = 100;

    private static readonly Regex IdRegex = new(
        pattern: @"^\w+([.-]\w+)*$",
        options: RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeout: TimeSpan.FromSeconds(10));

    public static bool IsValidPackageId(string packageId)
    {
        if (packageId == null)
        {
            throw new ArgumentNullException(nameof(packageId));
        }

        return IdRegex.IsMatch(packageId);
    }

    public static void ValidatePackageId(string packageId)
    {
        if (packageId.Length > MaxPackageIdLength)
        {
            throw new ArgumentException("Id must not exceed 100 characters.");
        }

        if (!IsValidPackageId(packageId))
        {
            throw new ArgumentException(string.Format(
                CultureInfo.CurrentCulture,
                "The package ID '{0}' contains invalid characters. Examples of valid package IDs include 'MyPackage' and 'MyPackage.Sample'.",
                packageId));
        }
    }
}
