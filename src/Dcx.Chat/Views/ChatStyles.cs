using System.Windows;
using System.Windows.Markup;

namespace DcxManager.Chat.Views;

/// <summary>
/// The chat screen's own styles (0.6.41): CipherChat's Windows client's terminal pieces (its Themes/Chat.xaml: the
/// sidebar rows, the log text, the message box, the reaction chips), drawn with the app's theme colours and type ramp
/// so they follow every theme pack and the UI font size. Every template is complete, so no default (white) system look
/// can show through, also when a control is disabled.
/// </summary>
internal static class ChatStyles
{
    private static ResourceDictionary? _dict;

    /// <summary>One shared, parsed copy (UI thread).</summary>
    public static ResourceDictionary Dictionary => _dict ??= (ResourceDictionary)XamlReader.Parse(Xaml);

    private const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="ChatSideItem" TargetType="Button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource Fg}"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Padding" Value="10,5"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsControl}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="b" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" CornerRadius="{DynamicResource Radius}">
                        <ContentPresenter HorizontalAlignment="Stretch"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.5"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="ChatSideHeader" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsCaption}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource Muted}"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>

    <Style x:Key="ChatLogText" TargetType="TextBox">
        <Setter Property="IsReadOnly" Value="True"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource Fg}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource Accent}"/>
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsControl}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <ScrollViewer x:Name="PART_ContentHost" VerticalScrollBarVisibility="Disabled" HorizontalScrollBarVisibility="Disabled"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="ChatLogRich" TargetType="RichTextBox">
        <Setter Property="IsReadOnly" Value="True"/>
        <Setter Property="IsDocumentEnabled" Value="True"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource Fg}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource Accent}"/>
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsControl}"/>
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RichTextBox">
                    <ScrollViewer x:Name="PART_ContentHost" VerticalScrollBarVisibility="Disabled" HorizontalScrollBarVisibility="Disabled"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="ChatComposeBox" TargetType="RichTextBox">
        <Setter Property="Foreground" Value="{DynamicResource Fg}"/>
        <Setter Property="Background" Value="{DynamicResource Input}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource LineStrong}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource Fg}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource Accent}"/>
        <Setter Property="Padding" Value="8,6"/>
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsBase}"/>
        <Setter Property="MinHeight" Value="34"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RichTextBox">
                    <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="{DynamicResource Radius}">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource AccentDim}"/></Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter TargetName="bd" Property="Opacity" Value="0.5"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- A word in a log line that does something (retry, discard, reveal): accent text, no box -->
    <Style x:Key="ChatLink" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FontFamily" Value="{DynamicResource MonoFont}"/>
        <Setter Property="FontSize" Value="{DynamicResource FsCaption}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="Transparent" Padding="{TemplateBinding Padding}">
                        <TextBlock x:Name="t" Text="{TemplateBinding Content}" Foreground="{TemplateBinding Foreground}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="t" Property="TextDecorations" Value="Underline"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="t" Property="TextDecorations" Value="Underline"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
""";
}
