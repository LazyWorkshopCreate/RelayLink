[CmdletBinding()]
param([int]$Iterations = 210000)

$ErrorActionPreference = 'Stop'
if ($Iterations -lt 100000) { throw 'Iterations must be at least 100000.' }
$password = Read-Host -AsSecureString 'Dashboard administrator password'
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
try {
    $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($plainText, $salt, $Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    'PBKDF2-SHA256${0}${1}${2}' -f $Iterations, [Convert]::ToBase64String($salt), [Convert]::ToBase64String($hash)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
}
