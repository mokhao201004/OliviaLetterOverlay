using System.IO;
using System.Windows;

namespace OliviaLetterOverlay;

public partial class CharacterWindow : Window
{
    public CharacterWindow()
    {
        InitializeComponent();
        var characters = CharacterStore.List();
        CharacterPicker.ItemsSource = characters;
        CharacterPicker.SelectedItem = characters.First(character => character.Id == CharacterStore.Current.Id);
    }

    private void SwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CharacterPicker.SelectedItem is not CharacterProfile character)
        {
            return;
        }

        Apply(() => CharacterStore.Select(character.Id));
    }

    private void CreateButton_OnClick(object sender, RoutedEventArgs e) =>
        Apply(() => CharacterStore.Create(CharacterNameBox.Text, PersonaBox.Text));

    private void Apply(Action action)
    {
        try
        {
            action();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
        }
    }
}
