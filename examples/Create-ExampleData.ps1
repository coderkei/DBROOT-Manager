# Generate a fictional DBROOT for exploring the editor and taking screenshots.
$ErrorActionPreference = 'Stop'
$demoRoot = Join-Path $PSScriptRoot 'DBROOT'
New-Item -ItemType Directory -Path $demoRoot -Force | Out-Null
function Set-TextField([byte[]]$Buffer, [int]$Offset, [int]$Length, [string]$Text) {
    $textBytes = [Text.Encoding]::UTF8.GetBytes($Text)
    if ($textBytes.Length -ge $Length) { throw "Example text exceeds field length: $Text" }
    [Array]::Copy($textBytes, 0, $Buffer, $Offset, $textBytes.Length)
}
function New-Record([int]$Size, [string]$Path, [string]$Title, [string]$Album = '', [string]$Artist = '') {
    $record = [byte[]]::new($Size)
    Set-TextField $record 0 255 $Path
    Set-TextField $record 255 64 $Title
    if ($Size -eq 351) {
        Set-TextField $record 319 16 $Album
        Set-TextField $record 335 16 $Artist
    }
    return ,$record
}
function Write-Records([string]$Name, [object[]]$Records) {
    $stream = [IO.MemoryStream]::new()
    try {
        foreach ($record in $Records) { $stream.Write($record, 0, $record.Length) }
        [IO.File]::WriteAllBytes((Join-Path $demoRoot $Name), $stream.ToArray())
    } finally { $stream.Dispose() }
}
$songs = @(
    @('Moonlit Station', 'Night Lines', 'Luna Vale'),
    @('Last Train Home', 'Night Lines', 'Luna Vale'),
    @('Rain on Glass', 'Night Lines', 'Luna Vale'),
    @('Quiet Signals', 'Night Lines', 'Luna Vale'),
    @('Morning Tide', 'Coastlines', 'Paper Harbour'),
    @('Salt and Sunlight', 'Coastlines', 'Paper Harbour'),
    @('Open Water', 'Coastlines', 'Paper Harbour'),
    @('Postcards', 'Coastlines', 'Paper Harbour'),
    @('Neon Gardens', 'After Hours', 'Velvet Circuit'),
    @('Soft Static', 'After Hours', 'Velvet Circuit'),
    @('Midnight Arcade', 'After Hours', 'Velvet Circuit'),
    @('Small Hours', 'After Hours', 'Velvet Circuit'),
    @('Maple Street', 'Slow Days', 'Cedar Notes'),
    @('Window Seat', 'Slow Days', 'Cedar Notes'),
    @('Sunday Sketches', 'Slow Days', 'Cedar Notes'),
    @('Warm Light', 'Slow Days', 'Cedar Notes')
)
$audio = foreach ($song in $songs) {
    $playerPath = '1:\Music\{0}\{1}\{2}.mp3' -f $song[2], $song[1], $song[0]
    New-Record 351 $playerPath $song[0] $song[1] $song[2]
}
Write-Records 'db_audio.db' $audio
$likes = [byte[]]::new(319 * 32)
$chosen = @(0, 4, 8, 13, 2, 10)
for ($i = 0; $i -lt $chosen.Count; $i++) { [Array]::Copy($audio[$chosen[$i]], 0, $likes, $i * 319, 319) }
[IO.File]::WriteAllBytes((Join-Path $demoRoot 'db_like.db'), $likes)
Write-Records 'db_image.db' @(
    (New-Record 319 '1:\Pictures\Coast at dawn.jpg' 'Coast at dawn'),
    (New-Record 319 '1:\Pictures\City lights.jpg' 'City lights'),
    (New-Record 319 '1:\Pictures\Autumn walk.jpg' 'Autumn walk')
)
Write-Records 'db_ebook.db' @(
    (New-Record 324 '1:\Ebooks\Travel journal.txt' 'Travel journal'),
    (New-Record 324 '1:\Ebooks\Short stories.txt' 'Short stories')
)
Write-Records 'db_folder0.db' @(
    (New-Record 324 '1:\Music' 'Music'),
    (New-Record 324 '1:\Pictures' 'Pictures'),
    (New-Record 324 '1:\Ebooks' 'E-books')
)
$content = [byte[]]::new(696)
[Array]::Copy([BitConverter]::GetBytes([uint32]0x0100030F), 0, $content, 0, 4)
[Array]::Copy([BitConverter]::GetBytes([uint32]$chosen.Count), 0, $content, 0x30, 4)
[IO.File]::WriteAllBytes((Join-Path $demoRoot 'db_content.db'), $content)
Write-Host "Created demo DBROOT: $demoRoot (16 songs, 6 favourites, 32 favourite slots)."
