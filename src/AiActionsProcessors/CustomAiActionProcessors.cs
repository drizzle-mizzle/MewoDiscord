using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Данные, с которыми процессор получает управление.
/// Text — текст запроса без упоминания бота, с упоминаниями в виде @имя: именно он
/// подставляется вместо {{message}} в HIT_PROMPT действия, а потом в промпты процессора.
/// </summary>
public record CustomAiActionContext(
    SocketUserMessage Message,
    string Text,
    CustomAiAction Action,
    IMessage? Quoted = null);

/// <summary>
/// Реестр процессоров кастомных действий: ключ — имя файла действия без расширения
/// (custom_ai_actions/edit_profile_picture.ini → edit_profile_picture). Действие без
/// процессора считается недоделанным: пайп его пропустит с предупреждением в логе.
/// </summary>
public static class CustomAiActionProcessors
{
    private static readonly Dictionary<string, Func<CustomAiActionContext, Task>> _processors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["convert_media"] = ConvertMedia.RunAsync,
            ["download_video"] = DownloadVideo.RunAsync,
            ["edit_profile_picture"] = EditProfilePicture.RunAsync
        };

    public static Func<CustomAiActionContext, Task>? Find(string actionId) =>
        _processors.GetValueOrDefault(actionId);
}
