using System.Windows;
using System.Windows.Controls;

namespace SifuMovesetEditor;

public class MoveGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate EnemyHeaderTemplate { get; set; }
    public DataTemplate WeaponHeaderTemplate { get; set; }
    public DataTemplate MoveCardTemplate { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is GroupHeader header)
        {
            return header.Level switch
            {
                1 => EnemyHeaderTemplate,
                2 => WeaponHeaderTemplate,
                _ => MoveCardTemplate
            };
        }

        return MoveCardTemplate;
    }
}
