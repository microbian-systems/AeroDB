# Get all project files
Get-ChildItem -Path . -Filter "*.*proj" -Recurse | ForEach-Object {
    $proj = $_.FullName
    $content = Get-Content $proj
    $regex = 'PackageReference Include="([^"]*)" Version="([^"]*)"'
    
    foreach ($line in $content) {
        if ($line -match $regex) {
            $name = $Matches[1]
            $version = $Matches[2]
            
            # If not a pre-release version (doesn't contain '-')
            if ($version -notlike "*-*") {
                Write-Host "Updating package $name in $proj ..."
                dotnet add "$proj" package "$name"
            }
        }
    }
}
