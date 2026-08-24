using MewoDiscord.AiActionsProcessors;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты медиа-сессии: план должен пережить дорогу в БД и обратно, а строка файла —
/// разобраться в ту же запись. Диск здесь не трогается: проверяются чистые функции.
/// </summary>
public class MediaSessionTests
{
    [Fact]
    public void Media_ПланПереживаетСериализацию()
    {
        var plan = new FfmpegRunner.MediaPlan(
            "gif",
            Start: 2.5,
            End: 6,
            Crop: new FfmpegRunner.CropBox(192, 432, 1920, 1296),
            Width: 480,
            Fps: 15);

        var restored = MediaPlanParser.Parse(MediaPlanParser.Serialize(plan));

        Assert.Equal(plan, restored);
    }

    [Fact]
    public void Media_ПустойПланСериализуетсяВПустойОбъект()
    {
        var empty = new FfmpegRunner.MediaPlan();

        Assert.Equal("{}", MediaPlanParser.Serialize(empty));
        Assert.True(MediaPlanParser.Parse(MediaPlanParser.Serialize(empty))!.IsEmpty);

        // Звуковой план — не пустой, хотя чисел в нём нет
        var audio = new FfmpegRunner.MediaPlan(AudioOnly: true);
        Assert.True(MediaPlanParser.Parse(MediaPlanParser.Serialize(audio))!.AudioOnly);
    }

    [Fact]
    public void Media_ВСериализованномПланеНетТабуляцийИПереводовСтрок()
    {
        // На этом держится формат файла сессий: поля разделены табуляцией,
        // а план идёт последним. Формат приходит от модели и бывает каким угодно
        var nasty = new FfmpegRunner.MediaPlan("gif\tmp4\nwebm");
        var line = MediaPlanParser.Serialize(nasty);

        Assert.DoesNotContain('\t', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void Media_СтрокаСессииРазбираетсяОбратно()
    {
        var session = new MediaSession(
            AnchorMessageId: 111,
            ChannelId: 222,
            SourceMessageId: 333,
            Plan: """{"crop":{"x":192,"y":432,"w":1920,"h":1296}}""",
            UpdatedAt: new DateTimeOffset(2026, 8, 23, 16, 21, 34, TimeSpan.Zero));

        var restored = MediaSessionStore.Parse(MediaSessionStore.Format(session));

        Assert.NotNull(restored);
        Assert.Equal(session.AnchorMessageId, restored.AnchorMessageId);
        Assert.Equal(session.ChannelId, restored.ChannelId);
        Assert.Equal(session.SourceMessageId, restored.SourceMessageId);
        Assert.Equal(session.Plan, restored.Plan);
        Assert.Equal(session.UpdatedAt, restored.UpdatedAt);
    }

    [Fact]
    public void Media_ФотоСТелефонаСчитаетсяНеподвижнойКартинкой()
    {
        // ffprobe отдаёт для одиночного кадра длительность в одну сороковую секунды,
        // а не ноль. Проверка «строго ноль» отбраковывала обычный jpg, и творческая
        // правка вместо работы отвечала «не понял, что сделать»
        var photo = FfmpegRunner.ParseProbe("""
            {"streams":[{"codec_type":"video","codec_name":"mjpeg","width":3072,"height":4096,
                         "avg_frame_rate":"25/1"}],
             "format":{"duration":"0.040000","format_name":"image2","size":"7800000"}}
            """);

        Assert.NotNull(photo);
        Assert.True(ConvertMedia.IsStillImage(photo));
    }

    [Fact]
    public void Media_ВидеоИГифкаМоделиНеОтдаются()
    {
        // Покадровая перерисовка модели не по силам — про это она честно отказывает
        var clip = new FfmpegRunner.MediaInfo(
            640,
            360,
            12,
            Video: new FfmpegRunner.VideoStreamInfo("h264", 640, 360, 30, 800_000));

        Assert.False(ConvertMedia.IsStillImage(clip));

        // Звук без картинки перерисовывать тем более нечем
        var audio = new FfmpegRunner.MediaInfo(
            0,
            0,
            120,
            Audio: new FfmpegRunner.AudioStreamInfo("aac", 2, 44100, 128_000));

        Assert.False(ConvertMedia.IsStillImage(audio));
    }

    [Fact]
    public void Media_БитаяСтрокаСессииПропускается()
    {
        Assert.Null(MediaSessionStore.Parse(string.Empty));
        Assert.Null(MediaSessionStore.Parse("111\t222\t333"));
        Assert.Null(MediaSessionStore.Parse("не число\t222\t333\t0\t{}"));
    }
}
