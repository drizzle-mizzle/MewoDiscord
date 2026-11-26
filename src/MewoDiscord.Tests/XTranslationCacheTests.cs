using System.Reflection;

using Discord;

using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Автономные проверки TTL, LRU и файлового хранилища переводов. Сеть и Discord API не вызываются;
/// IUserMessage нужен только как носитель snowflake, поэтому подменяется локальным proxy.
/// </summary>
public class XTranslationCacheTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("🔄", true)]
    [InlineData("🔄️", true)]
    [InlineData("🌐", false)]
    public void X_ПереключениеПринимаетОбаНаписанияЭмодзи(string name, bool expected)
    {
        Assert.Equal(expected, XTranslationHandler.IsToggleReaction(name));
    }

    [Fact]
    public void X_ОбращениеНеПродлеваетДесятиминутныйСрок()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var entry = cache.AddOrGet(Context(1), _now);

            Assert.True(cache.TryGet(1, _now.AddMinutes(9), out var known));
            Assert.Equal(_now.AddMinutes(10), known!.ExpiresAt);
            Assert.Equal(_now.AddMinutes(9), known.LastAccessAt);
            Assert.False(cache.TryGet(1, _now.AddMinutes(10), out _));
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void X_ДвадцатьПерваяКарточкаВытесняетДавноНеИспользованную()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);

            for (ulong id = 1; id <= XTranslationMessageCache.MaxEntries; id++)
            {
                cache.AddOrGet(Context(id), _now.AddSeconds(id));
            }

            Assert.True(cache.TryGet(1, _now.AddMinutes(2), out _));
            cache.AddOrGet(Context(21), _now.AddMinutes(3));

            Assert.Equal(XTranslationMessageCache.MaxEntries, cache.Count);
            Assert.True(cache.TryGet(1, _now.AddMinutes(4), out _));
            Assert.False(cache.TryGet(2, _now.AddMinutes(4), out _));
            Assert.True(cache.TryGet(21, _now.AddMinutes(4), out _));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task X_ПросрочкаУдаляетЗаписьИФайлыСДиска()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var entry = cache.AddOrGet(Context(1), _now);
            var image = await entry.SaveImageAsync("original", 1, [1, 2, 3], "image/png");

            Assert.True(File.Exists(image.FilePath));
            cache.CleanupExpired(_now.AddMinutes(10));

            Assert.Equal(0, cache.Count);
            Assert.False(Directory.Exists(entry.DirectoryPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void X_СтартоваяОчисткаУдаляетОсиротевшиеКаталоги()
    {
        var root = TempDirectory();

        try
        {
            var orphan = Directory.CreateDirectory(Path.Combine(root, "old-message"));
            var orphanFile = Path.Combine(orphan.FullName, "orphan.png");
            File.WriteAllBytes(orphanFile, [1, 2, 3]);
            var cache = new XTranslationMessageCache(root);

            cache.CleanupStorage();

            Assert.True(Directory.Exists(root));
            Assert.False(File.Exists(orphanFile));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void X_ОткатИспользуетСохранённыеТекстИКартинкиИРазблокируетКнопки()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var entry = cache.AddOrGet(Context(1, "Original", [Photo(1)]), _now);
            XTranslationImage[] originals = [new("original.png", "x-original-1.png", "image/png", 3)];
            XTranslationImage[] translations = [new("translated.png", "x-translated-1.png", "image/png", 3)];

            Assert.True(entry.TryStartText());
            Assert.True(entry.TryStartImages());
            entry.StoreTranslatedCaption("Перевод");
            entry.StoreImageVariants(originals, translations);
            entry.SetView(true);

            Assert.Equal("Перевод", entry.PostFor(entry.IsTranslated).Caption);
            Assert.Same(translations, entry.ImagesFor(entry.IsTranslated));
            Assert.False(entry.TryStartText());
            Assert.False(entry.TryStartImages());

            entry.SetView(false);

            Assert.Equal("Original", entry.PostFor(entry.IsTranslated).Caption);
            Assert.Same(originals, entry.ImagesFor(entry.IsTranslated));
            Assert.True(entry.HasTranslation);
            Assert.True(entry.TryStartText());
            Assert.True(entry.TryStartImages());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void X_ВосстановленныйПереводТребуетТолькоОригинальныеКартинки()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var entry = cache.AddOrGet(Context(1, "Original", [Photo(1)]), _now);

            entry.SetRestoredView(translated: true, translatedImages: true);

            Assert.True(entry.IsTranslated);
            Assert.True(entry.NeedsOriginalImageDownload);
            Assert.False(entry.HasTranslation);
            Assert.Empty(entry.TranslatedImages);
            Assert.Equal("Original", entry.PostFor(false).Caption);

            entry.SetView(false);

            Assert.False(entry.NeedsOriginalImageDownload);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task X_ОткатВозвращаетДоступныеРаздельныеРеакции()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var both = cache.AddOrGet(Context(1, "Original", [Photo(1)]), _now);
            var textOnly = cache.AddOrGet(Context(2, "Original"), _now);
            var imagesOnly = cache.AddOrGet(Context(3, "", [Photo(1)]), _now);

            await XTranslationHandler.RestoreTranslationReactionsAsync(both);
            await XTranslationHandler.RestoreTranslationReactionsAsync(textOnly);
            await XTranslationHandler.RestoreTranslationReactionsAsync(imagesOnly);

            Assert.Equal(["🌐", "🖼️"], AddedReactions(both));
            Assert.Equal(["🌐"], AddedReactions(textOnly));
            Assert.Equal(["🖼️"], AddedReactions(imagesOnly));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static PostMediaHandler.PostSentMessage Context(
        ulong id,
        string caption = "Текст",
        IReadOnlyList<SocialMedia>? media = null)
    {
        var message = DispatchProxy.Create<IUserMessage, MessageProxy>();
        ((MessageProxy)(object)message).Id = id;
        var post = new SocialPost("Автор", "@author", caption, media ?? [], null);
        var style = new PostMediaHandler.PostStyle(
            default,
            "x",
            () => "X",
            () => null,
            (_, _) => string.Empty);

        return new PostMediaHandler.PostSentMessage(
            message,
            post,
            $"https://x.com/author/status/{id}",
            [],
            [],
            style,
            null,
            true);
    }

    private static SocialMedia Photo(int index) =>
        new($"https://pbs.twimg.com/media/{index}.jpg", IsVideo: false, ThumbnailUrl: null);

    private static List<string> AddedReactions(XTranslationMessage entry) =>
        ((MessageProxy)(object)entry.Context.Message).AddedReactions;

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mewo-x-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public class MessageProxy : DispatchProxy
    {
        public ulong Id { get; set; }

        public List<string> AddedReactions { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Id")
            {
                return Id;
            }

            if (targetMethod?.Name == nameof(IUserMessage.AddReactionAsync) &&
                args?.FirstOrDefault() is IEmote emote)
            {
                AddedReactions.Add(emote.Name);
                return Task.CompletedTask;
            }

            if (targetMethod?.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
