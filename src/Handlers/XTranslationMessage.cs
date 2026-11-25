using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Файл одного варианта карточки. Байты лежат на диске, в памяти остаются только путь и метаданные.
/// </summary>
internal sealed record XTranslationImage(string FilePath, string FileName, string MimeType, long SizeBytes);

/// <summary>
/// Состояние одной X-карточки: оригинал, перевод и текущий показанный вариант.
/// Срок фиксирован при создании и не продлевается обращениями.
/// </summary>
internal sealed class XTranslationMessage
{
    public XTranslationMessage(
        PostMediaHandler.PostSentMessage context,
        string directoryPath,
        DateTimeOffset now)
    {
        Context = context;
        DirectoryPath = directoryPath;
        CreatedAt = now;
        LastAccessAt = now;
        ExpiresAt = now + XTranslationMessageCache.Lifetime;
    }

    public PostMediaHandler.PostSentMessage Context { get; }

    public string DirectoryPath { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastAccessAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; }

    public string? TranslatedCaption { get; private set; }

    public IReadOnlyList<XTranslationImage> OriginalImages { get; private set; } = [];

    public IReadOnlyList<XTranslationImage> TranslatedImages { get; private set; } = [];

    public bool IsTranslated { get; private set; }

    public bool TextTranslationStarted { get; private set; }

    public bool ImageTranslationStarted { get; private set; }

    public SemaphoreSlim OperationLock { get; } = new(1, 1);

    public bool CanTranslateText => XTranslationHandler.HasTranslatableText(Context.Post);

    public bool CanTranslateImages => XTranslationHandler.CanTranslateImages(Context.Post, Context.IsContainer);

    public bool HasTranslation => TranslatedCaption != null || TranslatedImages.Count > 0;

    /// <summary>
    /// UI-признак: после восстановления оригинала байтов перевода может уже не быть,
    /// но 🔄 должен остаться и по требованию создать перевод заново.
    /// </summary>
    public bool TranslationExpected { get; private set; }

    public void Touch(DateTimeOffset now) => LastAccessAt = now;

    public bool TryStartText()
    {
        if (!CanTranslateText || TextTranslationStarted)
        {
            return false;
        }

        TextTranslationStarted = true;
        return true;
    }

    public bool TryStartImages()
    {
        if (!CanTranslateImages || ImageTranslationStarted)
        {
            return false;
        }

        ImageTranslationStarted = true;
        return true;
    }

    public string? CaptionFor(bool translated, string? newTranslation = null)
    {
        var knownTranslation = newTranslation ?? TranslatedCaption;
        return translated && knownTranslation != null ? knownTranslation : Context.Post.Caption;
    }

    public IReadOnlyList<XTranslationImage> ImagesFor(
        bool translated,
        IReadOnlyList<XTranslationImage>? newOriginals = null,
        IReadOnlyList<XTranslationImage>? newTranslations = null)
    {
        var originals = newOriginals ?? OriginalImages;
        var translations = newTranslations ?? TranslatedImages;
        return translated && translations.Count > 0 ? translations : originals;
    }

    public SocialPost PostFor(bool translated, string? newTranslation = null) =>
        Context.Post with { Caption = CaptionFor(translated, newTranslation) };

    public void StoreTranslatedCaption(string caption)
    {
        TranslatedCaption = caption;
        TranslationExpected = true;
    }

    public void StoreImageVariants(
        IReadOnlyList<XTranslationImage> originals,
        IReadOnlyList<XTranslationImage> translations)
    {
        OriginalImages = originals;
        TranslatedImages = translations;
        TranslationExpected = true;
    }

    public void StoreOriginalImages(IReadOnlyList<XTranslationImage> originals) => OriginalImages = originals;

    public void StoreTranslatedImages(IReadOnlyList<XTranslationImage> translations)
    {
        TranslatedImages = translations;
        TranslationExpected = true;
    }

    public void SetView(bool translated)
    {
        IsTranslated = translated;

        if (translated)
        {
            TranslationExpected = true;
        }
    }

    public async Task<XTranslationImage> SaveImageAsync(
        string variant,
        int index,
        byte[] content,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var fileName = $"x-{variant}-{index}.{ImageExtension(mimeType)}";
        var filePath = Path.Combine(DirectoryPath, fileName);
        await File.WriteAllBytesAsync(filePath, content, cancellationToken);
        return new XTranslationImage(filePath, fileName, mimeType, content.LongLength);
    }

    public void DeleteFiles()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static string ImageExtension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "png"
    };
}
