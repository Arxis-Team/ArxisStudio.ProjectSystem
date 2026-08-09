using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FormsDesigner.ViewModels;

/// <summary>One of the shapes a new project can start in.</summary>
public sealed record ProjectTemplate(string Key, string Name, string Summary);

/// <summary>
/// Writes a new Avalonia application to disk.
/// </summary>
/// <remarks>
/// <para>
/// Written here rather than delegated to <c>dotnet new</c>: the templates that produce an Avalonia
/// application are a workload somebody has to have installed, and a studio whose New Project fails
/// on a clean machine with "no templates matched" has failed at the first thing it offers. What it
/// writes is a plain SDK project — the same four packages the Avalonia templates use — so the
/// result builds with nothing but the .NET SDK.
/// </para>
/// <para>
/// The project turns central package management off for itself. A new project put inside a
/// repository that manages versions centrally would otherwise fail to restore, with an error about
/// the <c>Version</c> attributes this file writes — true, and unhelpful when the folder was chosen
/// in a dialog that said nothing about it.
/// </para>
/// <para>
/// Every template produces a window with something in it. An empty canvas is a worse first
/// impression than a form somebody can immediately take apart, and the point of the designer is
/// that taking it apart is how you learn what it does.
/// </para>
/// </remarks>
public static class ProjectScaffold
{
    /// <summary>The Avalonia the generated project references.</summary>
    /// <remarks>
    /// The same version this designer is built against, deliberately. The adapter loads the
    /// project's own assemblies to build its forms, and two Avalonia versions in one process produce
    /// two <c>Button</c> types that are not assignable to one another.
    /// </remarks>
    public const string AvaloniaVersion = "12.1.1";

    private const string TargetFramework = "net10.0";

    public static IReadOnlyList<ProjectTemplate> Templates { get; } =
    [
        new("blank", "Пустое приложение", "Окно + MVVM каркас"),
        new("chat", "Чат", "Диалоги, список, ввод"),
        new("dashboard", "Дашборд", "Панели и графики"),
        new("media", "Медиаплеер", "Плейлист и плеер"),
    ];

    /// <summary>Whether a name can be a project — and therefore a namespace and a directory.</summary>
    public static bool IsUsableName(string name) =>
        name is { Length: > 0 }
            && char.IsLetter(name[0])
            && name.All(static c => char.IsLetterOrDigit(c) || c is '_' or '.')
            && !name.EndsWith('.');

    /// <summary>
    /// Writes the project and returns the file that names it.
    /// </summary>
    /// <exception cref="IOException">The directory exists and is not empty.</exception>
    public static async Task<string> CreateAsync(
        ProjectTemplate template,
        string parentDirectory,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);

        if (!IsUsableName(name))
        {
            throw new ArgumentException(
                "A project name starts with a letter and holds letters, digits, dots and underscores.",
                nameof(name));
        }

        string root = Path.Combine(parentDirectory, name);

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"{root} already exists and is not empty.");
        }

        Directory.CreateDirectory(Path.Combine(root, "Views"));
        Directory.CreateDirectory(Path.Combine(root, "ViewModels"));

        string project = Path.Combine(root, name + ".csproj");

        await WriteAsync(project, Csproj(), cancellationToken).ConfigureAwait(false);
        await WriteAsync(Path.Combine(root, "Program.cs"), Program(name), cancellationToken).ConfigureAwait(false);
        await WriteAsync(Path.Combine(root, "App.axaml"), AppMarkup(name), cancellationToken).ConfigureAwait(false);
        await WriteAsync(Path.Combine(root, "App.axaml.cs"), AppCode(name), cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"),
            ViewModel(name, template),
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            Path.Combine(root, "Views", "MainWindow.axaml"),
            Window(name, template),
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            Path.Combine(root, "Views", "MainWindow.axaml.cs"),
            WindowCode(name),
            cancellationToken).ConfigureAwait(false);

        return project;
    }

    /// <summary>The Avalonia version a project references, for the recent list's chip.</summary>
    public static string VersionOf(string projectFile)
    {
        try
        {
            foreach (string line in File.ReadLines(projectFile))
            {
                int avalonia = line.IndexOf("\"Avalonia\"", StringComparison.Ordinal);

                if (avalonia < 0)
                {
                    continue;
                }

                int version = line.IndexOf("Version=\"", avalonia, StringComparison.Ordinal);

                if (version < 0)
                {
                    continue;
                }

                version += "Version=\"".Length;

                int end = line.IndexOf('"', version);

                if (end > version)
                {
                    return "Avalonia " + line[version..end];
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The chip is decoration; a file that cannot be read simply has none.
        }

        return string.Empty;
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

    private static string Csproj() => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>{TargetFramework}</TargetFramework>
            <Nullable>enable</Nullable>
            <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
            <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>

            <!-- Its own versions, so that a folder inside a repository that manages them centrally
                 does not turn every reference below into a restore error. -->
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Avalonia" Version="{AvaloniaVersion}" />
            <PackageReference Include="Avalonia.Desktop" Version="{AvaloniaVersion}" />
            <PackageReference Include="Avalonia.Themes.Fluent" Version="{AvaloniaVersion}" />
            <PackageReference Include="Avalonia.Fonts.Inter" Version="{AvaloniaVersion}" />
          </ItemGroup>

        </Project>

        """);

    private static string Program(string name) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        using Avalonia;

        namespace {{name}};

        internal static class Program
        {
            [System.STAThread]
            public static void Main(string[] args) => BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

            public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }

        """);

    private static string AppMarkup(string name) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        <Application xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="{name}.App"
                     RequestedThemeVariant="Dark">

          <Application.Styles>
            <FluentTheme />
          </Application.Styles>

        </Application>

        """);

    private static string AppCode(string name) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        using Avalonia;
        using Avalonia.Controls.ApplicationLifetimes;
        using Avalonia.Markup.Xaml;
        using {{name}}.ViewModels;
        using {{name}}.Views;

        namespace {{name}};

        public partial class App : Application
        {
            public override void Initialize() => AvaloniaXamlLoader.Load(this);

            public override void OnFrameworkInitializationCompleted()
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.MainWindow = new MainWindow
                    {
                        DataContext = new MainWindowViewModel(),
                    };
                }

                base.OnFrameworkInitializationCompleted();
            }
        }

        """);

    private static string WindowCode(string name) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        using Avalonia.Controls;

        namespace {{name}}.Views;

        public partial class MainWindow : Window
        {
            public MainWindow() => InitializeComponent();
        }

        """);

    /// <summary>
    /// The view model behind the window, with whatever the template's form binds to.
    /// </summary>
    /// <remarks>
    /// Plain properties and a list, and no framework: a designer's templates should not decide which
    /// MVVM library somebody uses, and the point here is the markup rather than the plumbing.
    /// </remarks>
    private static string ViewModel(string name, ProjectTemplate template)
    {
        string body = template.Key switch
        {
            "chat" => """
                    public string Title { get; } = "Чат";

                    public ObservableCollection<string> Messages { get; } =
                    [
                        "Привет! Это шаблон чата.",
                        "Список, поле ввода и кнопка — всё, что нужно, чтобы начать.",
                    ];

                    public string Draft { get; set; } = string.Empty;
            """,

            "dashboard" => """
                    public string Title { get; } = "Дашборд";

                    public string Revenue { get; } = "1 284 900 ₽";

                    public string Orders { get; } = "3 471";

                    public string Conversion { get; } = "4,8 %";
            """,

            "media" => """
                    public string Title { get; } = "Медиаплеер";

                    public ObservableCollection<string> Playlist { get; } =
                    [
                        "01 — Введение",
                        "02 — Основная тема",
                        "03 — Финал",
                    ];

                    public string NowPlaying { get; } = "02 — Основная тема";
            """,

            _ => """
                    public string Title { get; } = "Готово к работе";

                    public string Greeting { get; } = "Откройте Views/MainWindow.axaml в конструкторе.";
            """,
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            using System.Collections.ObjectModel;

            namespace {{name}}.ViewModels;

            public sealed class MainWindowViewModel
            {
            {{body}}
            }

            """);
    }

    /// <summary>The form itself, which is what the designer opens first.</summary>
    private static string Window(string name, ProjectTemplate template)
    {
        string content = template.Key switch
        {
            "chat" => """
                  <DockPanel Margin="16">
                    <TextBlock DockPanel.Dock="Top" Text="{Binding Title}" FontSize="20" FontWeight="SemiBold"
                               Margin="0,0,0,12" />
                    <Grid DockPanel.Dock="Bottom" ColumnDefinitions="*,Auto" Margin="0,12,0,0">
                      <TextBox PlaceholderText="Сообщение…" Text="{Binding Draft}" />
                      <Button Grid.Column="1" Content="Отправить" Margin="8,0,0,0" />
                    </Grid>
                    <ListBox ItemsSource="{Binding Messages}" />
                  </DockPanel>
            """,

            "dashboard" => """
                  <Grid Margin="16" RowDefinitions="Auto,*" ColumnDefinitions="*,*,*" ColumnSpacing="12"
                        RowSpacing="12">
                    <TextBlock Grid.ColumnSpan="3" Text="{Binding Title}" FontSize="20" FontWeight="SemiBold" />
                    <Border Grid.Row="1" CornerRadius="8" Padding="16" Background="#22FFFFFF">
                      <StackPanel Spacing="6">
                        <TextBlock Text="Выручка" Opacity="0.7" />
                        <TextBlock Text="{Binding Revenue}" FontSize="22" FontWeight="SemiBold" />
                      </StackPanel>
                    </Border>
                    <Border Grid.Row="1" Grid.Column="1" CornerRadius="8" Padding="16" Background="#22FFFFFF">
                      <StackPanel Spacing="6">
                        <TextBlock Text="Заказы" Opacity="0.7" />
                        <TextBlock Text="{Binding Orders}" FontSize="22" FontWeight="SemiBold" />
                      </StackPanel>
                    </Border>
                    <Border Grid.Row="1" Grid.Column="2" CornerRadius="8" Padding="16" Background="#22FFFFFF">
                      <StackPanel Spacing="6">
                        <TextBlock Text="Конверсия" Opacity="0.7" />
                        <TextBlock Text="{Binding Conversion}" FontSize="22" FontWeight="SemiBold" />
                      </StackPanel>
                    </Border>
                  </Grid>
            """,

            "media" => """
                  <Grid Margin="16" ColumnDefinitions="220,*" ColumnSpacing="12">
                    <ListBox ItemsSource="{Binding Playlist}" />
                    <DockPanel Grid.Column="1">
                      <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Spacing="8"
                                  HorizontalAlignment="Center" Margin="0,12,0,0">
                        <Button Content="⏮" />
                        <Button Content="▶" />
                        <Button Content="⏭" />
                      </StackPanel>
                      <Border CornerRadius="8" Background="#22FFFFFF">
                        <TextBlock Text="{Binding NowPlaying}" HorizontalAlignment="Center"
                                   VerticalAlignment="Center" FontSize="18" />
                      </Border>
                    </DockPanel>
                  </Grid>
            """,

            _ => """
                  <StackPanel Margin="24" Spacing="10" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Title}" FontSize="22" FontWeight="SemiBold" />
                    <TextBlock Text="{Binding Greeting}" Opacity="0.75" />
                  </StackPanel>
            """,
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="using:{name}.ViewModels"
                    x:Class="{name}.Views.MainWindow"
                    x:DataType="vm:MainWindowViewModel"
                    Title="{name}"
                    Width="900" Height="600">

            {content}

            </Window>

            """);
    }
}
