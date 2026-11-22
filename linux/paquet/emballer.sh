#!/bin/bash
# Usage, dans un conteneur Debian : emballer.sh VERSION
# /entree/publie  : les fichiers publiés par dotnet pour linux-x64
# /entree/paquet  : la fiche, l'entrée de menu, les icônes
# /sortie         : où poser le paquet .deb
set -e
version="$1"; nom="download-everything_${version}_amd64"
racine="/tmp/$nom"; rm -rf "$racine"
install -d "$racine/DEBIAN" "$racine/usr/lib/download-everything" "$racine/usr/bin" \
           "$racine/usr/share/applications" "$racine/usr/share/doc/download-everything"
# Le programme et ses bibliothèques. Seul le programme est exécutable.
cp -r /entree/publie/. "$racine/usr/lib/download-everything/"
find "$racine/usr/lib/download-everything" -type d -exec chmod 755 {} +
find "$racine/usr/lib/download-everything" -type f -exec chmod 644 {} +
chmod 755 "$racine/usr/lib/download-everything/download-everything"
rm -f "$racine/usr/lib/download-everything/"*.pdb
ln -s ../lib/download-everything/download-everything "$racine/usr/bin/download-everything"
install -m 644 /entree/paquet/download-everything.desktop "$racine/usr/share/applications/"
install -m 644 /entree/paquet/copyright "$racine/usr/share/doc/download-everything/"
for taille in 256 128 64 48; do
  install -D -m 644 "/entree/paquet/icone-$taille.png" "$racine/usr/share/icons/hicolor/${taille}x${taille}/apps/download-everything.png"
done
taille=$(du -sk "$racine/usr" | cut -f1)
sed -e "s/@VERSION@/$version/" -e "s/@TAILLE@/$taille/" /entree/paquet/control > "$racine/DEBIAN/control"
chmod 644 "$racine/DEBIAN/control"
dpkg-deb --build --root-owner-group -Zxz "$racine" "/sortie/$nom.deb" > /dev/null
echo "paquet : $nom.deb"
dpkg-deb --info "/sortie/$nom.deb" | sed -n '1,22p'
echo "contenu :"
dpkg-deb --contents "/sortie/$nom.deb" | awk '{print $1, $3, $6, $7, $8}' | grep -v '/$' | head -30
