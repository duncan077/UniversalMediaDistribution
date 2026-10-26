[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string] $Username,

    [Parameter(Mandatory)]
    [ValidateLength(1, 100)]
    [string] $DisplayName,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string] $ServerUrl,

    [Parameter()]
    [SecureString] $Password
)

$ErrorActionPreference = 'Stop'

$adminToken = [Environment]::GetEnvironmentVariable(
    'UMD_SERVER_ADMIN_TOKEN',
    [EnvironmentVariableTarget]::Process)
if ([string]::IsNullOrWhiteSpace($adminToken) -or
    $adminToken.Length -lt 32) {
    throw 'Set UMD_SERVER_ADMIN_TOKEN to the server admin token before creating an account.'
}

if ($null -eq $Password) {
    $Password = Read-Host `
        -Prompt "Password for $($Username.ToLowerInvariant())" `
        -AsSecureString
}

$passwordPointer = [IntPtr]::Zero
$plainPassword = $null
try {
    $passwordPointer =
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $plainPassword =
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    if ($plainPassword.Length -lt 10) {
        throw 'Passwords must contain at least 10 characters.'
    }

    $requestBody = [ordered] @{
        username = $Username
        password = $plainPassword
        displayName = $DisplayName
    } | ConvertTo-Json

    $baseUri = [Uri]::new($ServerUrl.TrimEnd('/') + '/')
    $accountUri = [Uri]::new($baseUri, 'api/v1/admin/accounts')
    $httpClient = [System.Net.Http.HttpClient]::new()
    try {
        $httpClient.DefaultRequestHeaders.Authorization =
            [System.Net.Http.Headers.AuthenticationHeaderValue]::new(
                'Bearer',
                $adminToken)
        $content = [System.Net.Http.StringContent]::new(
            $requestBody,
            [System.Text.Encoding]::UTF8,
            'application/json')
        $response = $httpClient.PostAsync($accountUri, $content).Result
        try {
            $body = $response.Content.ReadAsStringAsync().Result
            if (-not $response.IsSuccessStatusCode) {
                throw "Account creation failed ($([int]$response.StatusCode)): $body"
            }

            $account = $body | ConvertFrom-Json
        }
        finally {
            $response.Dispose()
            $content.Dispose()
        }
    }
    finally {
        $httpClient.Dispose()
    }
}
finally {
    $plainPassword = $null
    $requestBody = $null
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
}

Write-Host "Created @$($account.username) for $($account.displayName)."
Write-Host "Account ID: $($account.accountId)"
