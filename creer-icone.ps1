# Dessine l'icône de Download Everything et l'écrit dans icone.ico, en plusieurs tailles
# pour rester nette du bureau à la barre des tâches.
# Le dessin réunit deux signes en un seul trait : une flèche de téléchargement dont la pointe est une coche.
# Le trait descend, puis remonte à droite plus haut qu'à gauche, comme le « c'est fait » d'une case cochée.
# À relancer seulement si on veut changer le dessin.  pwsh -File creer-icone.ps1 [-Apercu fichier.png]
param([string]$Apercu)
Add-Type -AssemblyName System.Drawing

# Le dessin en 16 pixels, celui de la barre des tâches. À cette taille un trait réduit bave, chaque pixel
# est donc posé à la main : des rectangles (x, y, largeur, hauteur), sans lissage. La rangée 12 reste vide
# pour détacher le signe de la barre ambre.
function New-ImagePng16 {
    $image = [System.Drawing.Bitmap]::new(16, 16)
    $g = [System.Drawing.Graphics]::FromImage($image)
    $g.SmoothingMode = 'None'
    $g.Clear([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
    $blanc = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $ambre = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 176, 0))
    $signe = (5,2,2,9),                                   # hampe
             (2,7,2,1), (3,8,2,1), (4,9,2,1),             # bras gauche, court
             (6,11,2,1),                                  # pointe
             (7,10,2,1), (8,9,2,1), (9,8,2,1), (10,7,2,1), (11,6,2,1), (12,5,2,1), (13,4,1,1)   # bras droit, long
    foreach ($r in $signe) { $g.FillRectangle($blanc, $r[0], $r[1], $r[2], $r[3]) }
    $g.FillRectangle($ambre, 2, 13, 12, 1)
    $g.Dispose()
    $flux = [System.IO.MemoryStream]::new()
    $image.Save($flux, [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    return , $flux.ToArray()
}

function New-ImagePng([int]$taille) {
    if ($taille -eq 16) { return , (New-ImagePng16) }
    $image = [System.Drawing.Bitmap]::new($taille, $taille)
    $g = [System.Drawing.Graphics]::FromImage($image)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $u = $taille / 64.0   # le dessin est pensé sur une grille de 64, en multiples de 4 pour tomber sur des pixels entiers

    # Fond graphite aux angles presque nets, comme la fenêtre
    $rayon = 3 * $u
    $bord = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $c = $taille - 2 * $rayon
    $bord.AddArc(0, 0, 2 * $rayon, 2 * $rayon, 180, 90)
    $bord.AddArc($c, 0, 2 * $rayon, 2 * $rayon, 270, 90)
    $bord.AddArc($c, $c, 2 * $rayon, 2 * $rayon, 0, 90)
    $bord.AddArc(0, $c, 2 * $rayon, 2 * $rayon, 90, 90)
    $bord.CloseFigure()
    $fond = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
    $g.FillPath($fond, $bord)
    # Un filet d'un pixel, repris des filets de la fenêtre. Il détache le carré sur une barre des tâches sombre.
    $filet = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 58, 58, 58), 1)
    $g.DrawPath($filet, $bord)

    # Le trait. Bouts carrés et angles vifs, dans l'esprit de la fenêtre. Épaisseur 8, soit 3, 4 et 6 pixels
    # exacts en 24, 32 et 48.
    $plume = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single](8 * $u))
    $plume.StartCap = 'Flat'; $plume.EndCap = 'Flat'; $plume.LineJoin = 'Miter'; $plume.MiterLimit = 8

    # La hampe de la flèche, qui descend jusqu'au creux de la coche
    $g.DrawLine($plume, [single](28 * $u), [single](8 * $u), [single](28 * $u), [single](42 * $u))
    # La coche, qui sert de pointe. Le bras droit remonte une fois et demie plus haut que le gauche,
    # c'est ce qui fait lire « c'est fait » et pas seulement une flèche.
    $coche = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(14 * $u, 28 * $u),
        [System.Drawing.PointF]::new(28 * $u, 42 * $u),
        [System.Drawing.PointF]::new(50 * $u, 18 * $u))
    $g.DrawLines($plume, $coche)

    # Le trait d'arrivée porte la couleur signal de l'application, l'ambre. Un filet et non une dalle,
    # à bonne distance de la pointe pour que les deux formes ne se collent pas aux petites tailles.
    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 176, 0))
    $g.FillRectangle($accent, [single](12 * $u), [single](52 * $u), [single](40 * $u), [single](4 * $u))

    $g.Dispose()
    $flux = [System.IO.MemoryStream]::new()
    $image.Save($flux, [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    return , $flux.ToArray()
}

if ($Apercu) {
    # Une planche d'aperçu : le dessin en grand, puis aux petites tailles où il doit rester lisible.
    $planche = [System.Drawing.Bitmap]::new(520, 280)
    $g = [System.Drawing.Graphics]::FromImage($planche)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 60, 60, 60))
    $x = 12
    foreach ($t in 256, 64, 48, 32, 24, 16) {
        $img = [System.Drawing.Image]::FromStream([System.IO.MemoryStream]::new((New-ImagePng $t)))
        $g.DrawImage($img, $x, 12, $t, $t); $x += $t + 12; $img.Dispose()
    }
    $g.Dispose(); $planche.Save($Apercu, [System.Drawing.Imaging.ImageFormat]::Png); $planche.Dispose()
    "Aperçu écrit : $Apercu"
    return
}

$tailles = 16, 24, 32, 48, 64, 256
$images = foreach ($t in $tailles) { , (New-ImagePng $t) }

# Format .ico : un en-tête, une table d'entrées, puis les images PNG à la suite.
$sortie = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($sortie)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tailles.Count)
$position = 6 + 16 * $tailles.Count
for ($i = 0; $i -lt $tailles.Count; $i++) {
    $octet = if ($tailles[$i] -ge 256) { 0 } else { $tailles[$i] }   # 0 veut dire 256 dans ce format
    $w.Write([byte]$octet); $w.Write([byte]$octet); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$position)
    $position += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icone.ico'), $sortie.ToArray())
"icone.ico écrit, $($sortie.Length) octets"
