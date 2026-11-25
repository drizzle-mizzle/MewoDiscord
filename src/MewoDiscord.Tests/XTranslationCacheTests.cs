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
    public void X_ПереключениеХранитОригинальныйИПереведённыйТекст()
    {
        var root = TempDirectory();

        try
        {
            var cache = new XTranslationMessageCache(root);
            var entry = cache.AddOrGet(Context(1, "Original"), _now);
            entry.StoreTranslatedCaption("Перевод");
            entry.SetView(true);

            Assert.Equal("Перевод", entry.PostFor(entry.IsTranslated).Caption);
            Assert.True(entry.HasTranslation);

            entry.SetView(false);

            Assert.Equal("Original", entry.PostFor(entry.IsTranslated).Caption);
            Assert.True(entry.TranslationExpected);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static PostMediaHandler.PostSentMessage Context(ulong id, string caption = "Текст")
    {
        var message = DispatchProxy.Create<IUserMessage, MessageProxy>();
        ((MessageProxy)(object)message).Id = id;
        var post = new SocialPost("Автор", "@author", caption, [], null);
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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Id")
            {
                return Id;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
