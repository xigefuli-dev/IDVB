Set-StrictMode -Version Latest

function Get-IDVBReleaseLineParts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseLine
    )

    $match = [regex]::Match($ReleaseLine, '^b(?<major>\d{2})\.(?<minor>\d+)$')
    if (-not $match.Success) {
        throw "Invalid IDVB release line '$ReleaseLine'. Expected bNN.N."
    }

    return $match
}

function ConvertTo-IDVBPublicVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseLine,
        [Parameter(Mandatory)]
        [DateTimeOffset]$UtcNow,
        [ValidateRange(0, 9999)]
        [int]$BuildNumber = 0
    )

    [void](Get-IDVBReleaseLineParts -ReleaseLine $ReleaseLine)
    $chinaStandardTime = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
    $localTime = [TimeZoneInfo]::ConvertTime($UtcNow, $chinaStandardTime)
    $date = $localTime.ToString(
        'yy.MM.dd',
        [System.Globalization.CultureInfo]::InvariantCulture)
    $count = $BuildNumber.ToString(
        '0000',
        [System.Globalization.CultureInfo]::InvariantCulture)
    return '{0}-{1}.{2}' -f $ReleaseLine, $date, $count
}

function ConvertTo-IDVBNumericVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PublicVersion,
        [ValidateRange(0, 9999)]
        [int]$Patch = 0
    )

    $match = [regex]::Match(
        $PublicVersion,
        '^b(?<major>\d{2})\.(?<minor>\d+)-(?<year>\d{2})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<build>\d{4})$')
    if (-not $match.Success) {
        throw "Invalid IDVB public version '$PublicVersion'. Expected bNN.N-YY.MM.DD.NNNN."
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $year = [int]$match.Groups['year'].Value
    $month = [int]$match.Groups['month'].Value
    $day = [int]$match.Groups['day'].Value
    $build = [int]$match.Groups['build'].Value
    try {
        [void][DateTime]::new(2000 + $year, $month, $day)
    }
    catch {
        throw "Invalid IDVB release timestamp in '$PublicVersion': $($_.Exception.Message)"
    }

    return "$major.$minor.$Patch.$build"
}

function Get-IDVBProductVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseLine
    )

    $parts = Get-IDVBReleaseLineParts -ReleaseLine $ReleaseLine
    return "{0}.{1}.0" -f ([int]$parts.Groups['major'].Value), ([int]$parts.Groups['minor'].Value)
}

function ConvertFrom-IDVBProductVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProductVersion
    )

    $match = [regex]::Match(
        $ProductVersion,
        '^(?<major>\d{1,3})\.(?<minor>\d{1,3})\.(?<patch>\d{1,3})(?:\.(?<build>\d{1,3}))?(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    if (-not $match.Success) {
        throw "Invalid IDVB product version '$ProductVersion'. Expected N.N.N[-prerelease] or N.N.N.N[-prerelease]."
    }

    return [pscustomobject]@{
        Major = [int]$match.Groups['major'].Value
        Minor = [int]$match.Groups['minor'].Value
        Patch = [int]$match.Groups['patch'].Value
        Prerelease = $match.Groups['prerelease'].Value
        BaseVersion = "{0}.{1}.{2}" -f $match.Groups['major'].Value, $match.Groups['minor'].Value, $match.Groups['patch'].Value
    }
}

function Reserve-IDVBBuildNumber {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$CounterPath
    )

    $CounterPath = [System.IO.Path]::GetFullPath($CounterPath)
    $parentDirectory = Split-Path -Parent $CounterPath
    if (-not [string]::IsNullOrWhiteSpace($parentDirectory)) {
        [void][System.IO.Directory]::CreateDirectory($parentDirectory)
    }

    $lockPath = "$CounterPath.lock"
    $lockStream = $null
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        try {
            $lockStream = [System.IO.File]::Open(
                $lockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            break
        }
        catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 25
        }
    }

    if ($null -eq $lockStream) {
        throw "Could not acquire the IDVB build counter lock: $lockPath"
    }

    try {
        $rawValue = if (Test-Path -LiteralPath $CounterPath) {
            [System.IO.File]::ReadAllText($CounterPath).Trim()
        }
        else {
            '0'
        }

        if ([string]::IsNullOrWhiteSpace($rawValue)) { $rawValue = '0' }
        if ($rawValue -notmatch '^\d{1,4}$') {
            throw "Invalid IDVB build counter '$rawValue' in $CounterPath"
        }

        $nextValue = [int]$rawValue + 1
        if ($nextValue -gt 9999) {
            throw "IDVB build counter exhausted its four-digit range: $CounterPath"
        }

        [System.IO.File]::WriteAllText(
            $CounterPath,
            $nextValue.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            [System.Text.Encoding]::ASCII)
        return $nextValue
    }
    finally {
        $lockStream.Dispose()
    }
}

function New-IDVBBuildVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseLine,
        [Parameter(Mandatory)]
        [string]$CounterPath,
        [DateTimeOffset]$UtcNow = [DateTimeOffset]::UtcNow
    )

    $buildNumber = Reserve-IDVBBuildNumber -CounterPath $CounterPath
    return ConvertTo-IDVBPublicVersion -ReleaseLine $ReleaseLine -UtcNow $UtcNow -BuildNumber $buildNumber
}

Export-ModuleMember -Function ConvertTo-IDVBPublicVersion, ConvertTo-IDVBNumericVersion, Get-IDVBProductVersion, ConvertFrom-IDVBProductVersion, Reserve-IDVBBuildNumber, New-IDVBBuildVersion
