using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace YtDlpInterface;

/// <summary>
/// Pictogramme décoratif, tiré de la police d'icônes de Windows. Il n'a pas de présence pour les lecteurs
/// d'écran, qui annonceraient sinon un caractère sans nom. Le sens est porté par le libellé voisin.
/// </summary>
public sealed class Picto : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
