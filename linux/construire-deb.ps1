# Fabrique le paquet .deb de Download Everything pour Debian et Ubuntu (64 bits).
# Il faut le SDK .NET 10 et Docker. La compilation se fait ici, l'emballage dans un conteneur Debian jetable,
# parce que les droits des fichiers d'un paquet .deb ne peuvent pas être posés depuis Windows.
#   pwsh -File construire-deb.ps1 [-Sortie dossier]
param([string]$Sortie = (Join-Path $PSScriptRoot 'publication'))
$ErrorActionPreference = 'Stop'
$projet = Join-Path $PSScriptRoot 'DownloadEverything.Linux.csproj'
$version = ([xml](Get-Content $projet)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publie = Join-Path $Sortie 'publie'
New-Item -ItemType Directory -Force $Sortie | Out-Null
if (Test-Path $publie) { Get-ChildItem -LiteralPath $publie -Recurse -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force } }

"Compilation pour Linux, version $version"
# Un seul fichier pour le code, les quelques bibliothèques natives posées à côté : rien n'est déballé au lancement.
dotnet publish $projet -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -o $publie --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }

"Emballage dans un conteneur Debian"
docker run --rm -v "${publie}:/entree/publie:ro" -v "$(Join-Path $PSScriptRoot 'paquet'):/entree/paquet:ro" -v "${Sortie}:/sortie" debian:trixie-slim bash /entree/paquet/emballer.sh $version
if ($LASTEXITCODE -ne 0) { throw "L'emballage a échoué." }
$deb = Get-Item (Join-Path $Sortie "download-everything_${version}_amd64.deb")
"{0}  {1:N1} Mo  SHA-256 {2}" -f $deb.Name, ($deb.Length / 1MB), (Get-FileHash $deb.FullName -Algorithm SHA256).Hash.ToLower()