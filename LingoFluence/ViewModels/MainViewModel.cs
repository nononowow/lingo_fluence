using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LingoFluence.Models;
using LingoFluence.Services;
using Microsoft.Win32;

namespace LingoFluence.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly DatabaseService _db = new();
    private readonly AnkiParser _parser = new();

    private Deck? _selectedDeck;
    private bool _isImporting;
    private string _statusText = "";

    public ObservableCollection<Deck> Decks { get; } = new();

    public Deck? SelectedDeck
    {
        get => _selectedDeck;
        set { Set(ref _selectedDeck, value); OnPropertyChanged(nameof(CanStudy)); OnPropertyChanged(nameof(CanEditAi)); }
    }

    public bool IsImporting
    {
        get => _isImporting;
        set { Set(ref _isImporting, value); OnPropertyChanged(nameof(CanImport)); }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool CanStudy  => SelectedDeck != null &&
                              (SelectedDeck.IsAi
                                  ? SelectedDeck.TotalCards > 0
                                  : SelectedDeck.DueCards > 0 || SelectedDeck.NewCards > 0);
    public bool CanImport => !IsImporting;

    public ICommand ImportCommand  { get; }
    public ICommand StudyCommand   { get; }
    public ICommand DeleteCommand  { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenAiCommand  { get; }
    public ICommand EditAiCommand  { get; }

    public bool CanEditAi => SelectedDeck is { IsAi: true };

    public MainViewModel()
    {
        ImportCommand  = new RelayCommand(_ => ImportDeck(),    _ => CanImport);
        StudyCommand   = new RelayCommand(_ => StartStudy(),   _ => CanStudy);
        DeleteCommand  = new RelayCommand(_ => DeleteDeck(),   _ => SelectedDeck != null);
        RefreshCommand = new RelayCommand(_ => LoadDecks());
        OpenAiCommand  = new RelayCommand(_ => OpenAiWindow());
        EditAiCommand  = new RelayCommand(_ => EditAiDeck(), _ => CanEditAi);
        LoadDecks();
    }

    private void LoadDecks()
    {
        Decks.Clear();
        foreach (var d in _db.LoadDecks())
            Decks.Add(d);
        StatusText = Decks.Count == 0 ? "Import a .apkg file to get started." : "";
    }

    private async void ImportDeck()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Anki Package (*.apkg)|*.apkg",
            Title  = "Select Anki Deck",
            Multiselect = false
        };
        if (dlg.ShowDialog() != true) return;

        IsImporting = true;
        StatusText  = "Importing…";

        try
        {
            var apkgPath = dlg.FileName;
            await Task.Run(() =>
            {
                // Re-import of the same file replaces the old deck (and its media)
                // rather than creating a duplicate with stale data.
                foreach (var oldId in _db.FindDecksByImportPath(apkgPath))
                {
                    var oldMedia = _db.GetMediaFolder(oldId);
                    _db.DeleteDeck(oldId);
                    if (!string.IsNullOrEmpty(oldMedia) && Directory.Exists(oldMedia))
                        try { Directory.Delete(oldMedia, recursive: true); } catch { /* ignore */ }
                }

                // Create temporary media output folder (GUID) — will be renamed after db insert
                var tempMediaId  = Guid.NewGuid().ToString("N");
                var mediaFolder  = Path.Combine(DatabaseService.AppDataPath, "media", tempMediaId);
                var result       = _parser.Parse(apkgPath, mediaFolder);

                var deckId = _db.SaveDeck(result.DeckName, apkgPath, mediaFolder);

                // Now rename media folder to deckId-based path
                var finalMedia = Path.Combine(DatabaseService.AppDataPath, "media", deckId.ToString());
                if (Directory.Exists(mediaFolder) && !Directory.Exists(finalMedia))
                    Directory.Move(mediaFolder, finalMedia);
                else if (Directory.Exists(mediaFolder))
                    finalMedia = mediaFolder;

                _db.UpdateDeckMediaFolder(deckId, finalMedia);

                // Fix audio paths in rows to use final media folder
                var rows = result.Rows.Select(row =>
                {
                    var audio = row.AudioFile == null ? null
                        : Path.Combine(finalMedia, Path.GetFileName(row.AudioFile));
                    return (row.AnkiNoteId, row.Answer, row.Context, audio,
                            row.AnkiCardId, row.DueDate, row.Interval,
                            row.Ease, row.Reps, row.Lapses, row.State,
                            row.SentenceDe, row.WordEn, row.SentenceEn, chinese: "");
                });

                _db.SaveNotesAndCards(deckId, rows);
            });

            LoadDecks();
            StatusText = "Import complete!";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = "Import failed.";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void StartStudy()
    {
        if (SelectedDeck == null) return;
        if (SelectedDeck.IsAi)
        {
            var win = new Views.AiStudyWindow(SelectedDeck.Id, SelectedDeck.Name);
            win.ShowDialog();
        }
        else
        {
            var win = new Views.StudyWindow(SelectedDeck.Id, SelectedDeck.Name);
            win.ShowDialog();
        }
        LoadDecks(); // refresh stats after study
    }

    private void OpenAiWindow()
    {
        var win = new Views.AiWindow();
        win.ShowDialog();
        LoadDecks(); // deck list may have a new AI deck after import
    }

    private void EditAiDeck()
    {
        if (SelectedDeck is not { IsAi: true } deck) return;
        var win = new Views.AiWindow(deck.Id, deck.Name);
        win.ShowDialog();
        LoadDecks(); // cards/name may have changed
    }

    private void DeleteDeck()
    {
        if (SelectedDeck == null) return;
        var r = MessageBox.Show($"Delete deck \"{SelectedDeck.Name}\"?\nThis removes all review history.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _db.DeleteDeck(SelectedDeck.Id);
        LoadDecks();
    }
}

// Minimal relay command
public class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p)    => execute(p);
}
