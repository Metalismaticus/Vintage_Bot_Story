// Игровые типы берём поимённо: тянуть Vintagestory.API.Common целиком нельзя —
// там свой Func<>, он конфликтует с системным (см. Hands.cs, Wear.cs)
using Material = Vintagestory.API.Common.EnumBlockMaterial;
using Facing = Vintagestory.API.MathTools.BlockFacing;

namespace VsBotKit;

/// <summary>Почему добыча остановилась.</summary>
public enum QuarryStop
{
    /// <summary>План пройден до конца.</summary>
    Done,
    /// <summary>Отменили снаружи.</summary>
    Cancelled,
    /// <summary>Кончилось отведённое время.</summary>
    Timeout,
    /// <summary>Реестр блоков сервера ещё не пришёл — считать план не по чему.</summary>
    NotReady,
    /// <summary>Нет инструмента нужного тира.</summary>
    NoTool,
    /// <summary>Кирка вот-вот сломается, запасной нет.</summary>
    ToolWorn,
    /// <summary>Некуда складывать.</summary>
    InventoryFull,
    /// <summary>Рядом лава или кипяток.</summary>
    Lava,
    /// <summary>Над головой нестабильная порода — стоять опасно.</summary>
    Unstable,
    /// <summary>Из ямы не выбраться: обратный путь не находится.</summary>
    NoEscape
}

/// <summary>
/// КАК СНИМАТЬ ОБЪЁМ. Настройка роли, а не догадка механизма: два способа
/// дают РАЗНЫЙ результат в мире и разную цену, и выбирать между ними должен
/// человек.
/// </summary>
public enum QuarryCut
{
    /// <summary>
    /// ШАХМАТКОЙ — дешевле по добыче. Клетки берутся через одну по чётности
    /// (X+Y+Z): часть объёма уходит в крошку, зато у другой части пропадают
    /// ВСЕ ШЕСТЬ соседей, и она выпадает ЦЕЛЫМИ блоками (BreakIfFloating,
    /// разбор — в описании <see cref="Quarry"/>). Целый камень не надо ни
    /// обжигать, ни складывать по четыре: он приходит готовым строительным
    /// блоком.
    ///
    /// ЦЕЛЫМИ ВЫХОДИТ ЧЕТВЕРТЬ ОБЪЁМА, А НЕ ПОЛОВИНА. Ровная шахматка по всем
    /// трём осям дала бы половину, но копать её нельзя вовсе: каждая её клетка
    /// «в крошку», кроме самого верхнего слоя, замурована шестью «целыми», и
    /// кликнуть по ней не с чего. Поэтому каждый второй слой отдаётся целиком
    /// в жертву — ради подхода (<see cref="Quarry.IsHarvestLayer"/>).
    ///
    /// Цена — вид: пока карьер не пройден до конца, на месте стоит решётка из
    /// недобранных клеток, и со стороны это «яма недокопана». Ровно так это и
    /// увидел заказчик.
    /// </summary>
    Checkerboard,

    /// <summary>
    /// ЦЕЛИКОМ — ровная яма. Все клетки слоя ломаются подряд, объём снимается
    /// без остатка и без решётки.
    ///
    /// Цена — добыча: камень, у которого хоть один сосед на месте, выпадает
    /// КРОШКОЙ (2-3 обломка вместо блока), то есть целых блоков не будет
    /// вовсе. Берут этот способ, когда нужна яма (котлован, площадка, ров), а
    /// не камень.
    /// </summary>
    Whole
}

/// <summary>
/// План карьера: что копать «на крошку», что — последним, целым блоком,
/// и какие клетки трогать нельзя (пандус на выход).
/// </summary>
public sealed class QuarryPlan
{
    /// <summary>Нижний угол области (включительно).</summary>
    public required BlockPos Min { get; init; }

    /// <summary>Верхний угол области (включительно).</summary>
    public required BlockPos Max { get; init; }

    /// <summary>Чётность (X+Y+Z)&amp;1, которую собираем целыми блоками.</summary>
    public required int Parity { get; init; }

    /// <summary>Клетки-«разделители»: их ломают первыми, они дают крошку.</summary>
    public required IReadOnlyList<BlockPos> Clear { get; init; }

    /// <summary>
    /// Клетки, которые к моменту слома останутся без единого твёрдого соседа —
    /// только они дают ЦЕЛЫЙ блок.
    /// </summary>
    public required IReadOnlyList<BlockPos> Harvest { get; init; }

    /// <summary>Ступени пандуса: никогда не ломаются, по ним бот выходит наверх.</summary>
    public required IReadOnlyCollection<BlockPos> Ramp { get; init; }

    /// <summary>
    /// Каким способом снимается объём. Хранится в самом плане нарочно: остаток
    /// прерванной работы (<see cref="Quarry.Remaining"/>) — это ТОТ ЖЕ карьер,
    /// и переключенная тем временем настройка не должна менять его на полпути.
    /// </summary>
    public QuarryCut Cut { get; init; } = QuarryCut.Checkerboard;

    public int Total => Clear.Count + Harvest.Count;

    public override string ToString() =>
        $"карьер {Min}..{Max}: копать {Total} кл. " +
        (Cut == QuarryCut.Whole
            ? "целиком, без шахматки (ровная яма, весь камень крошкой)"
            : $"(целыми {Harvest.Count}, крошкой {Clear.Count})") +
        $", пандус {Ramp.Count} кл.";
}

/// <summary>Итог работы карьера — честный, по фактическому приросту в сумке.</summary>
public sealed record QuarryReport(
    QuarryStop Stop,
    string Reason,
    BlockPos StoppedAt,
    int Planned,
    int Broken,
    int Isolated,
    int AutoDropped,
    int Skipped,
    int WholeGained,
    int RubbleGained,
    int DurabilitySpent,
    double Seconds,
    IReadOnlyDictionary<string, int> Gained,
    IReadOnlyDictionary<string, int> Skips)
{
    /// <summary>
    /// Клетки, которых в мире БОЛЬШЕ НЕТ: сломали сами или нашли уже пустыми.
    ///
    /// Нужно ради возврата к прерванной работе. Пропущенные клетки сюда НЕ
    /// входят нарочно: «не изолирован» — это «рано», а не «нельзя», и на
    /// следующем заходе, когда слой снизу уже убран, такая клетка как раз и
    /// даёт целый блок. Выкинуть её из остатка значило бы потерять ровно то,
    /// ради чего затевалась шахматка.
    /// </summary>
    public IReadOnlyCollection<BlockPos> Done { get; init; } = [];

    /// <summary>Сколько раз к работе возвращались после перерыва.</summary>
    public int Returns { get; init; }

    /// <summary>
    /// ПОМЕХА ОБЩИМ СЛОВОМ: пройдёт ли она сама (<see cref="Setback"/>).
    ///
    /// Не вторая копия <see cref="Stop"/>, а ответ на ДРУГОЙ вопрос — тот же
    /// разбор, что у дороги (<see cref="RoadReport.Hindrance"/>).
    /// <see cref="Stop"/> отвечает человеку («лава в (…) — дальше не копаю»),
    /// <see cref="Hindrance"/> отвечает возврату: ждать бесполезно или подожди
    /// и возьмись заново. Считает одно чистое правило
    /// (<see cref="Quarry.WhatStopped"/>), своего суждения у отчёта тут нет.
    /// </summary>
    public Setback Hindrance { get; init; } = Setback.None;

    /// <summary>
    /// ЖЕРТВЫ: блоки ЗА ГРАНИЦЕЙ плана, сломанные только ради подхода к клетке.
    ///
    /// Своя же клетка «в крошку», взятая раньше срока, сюда не идёт: её и так
    /// ломать, это обычная работа, и она считается в <see cref="Broken"/>.
    /// Здесь — то, за что действительно заплачено.
    ///
    /// Число стоит в отчёте, потому что это ОСОЗНАННАЯ ЦЕНА, а не случайность:
    /// заказчик сам её и назвал — «придётся принести какие-то в жертву, чтобы
    /// дотянуться». Молчащая жертва выглядит как «бот зачем-то ломает лишнее»;
    /// названная — как счёт, который человек может проверить и урезать
    /// настройкой <see cref="Quarry.MaxSacrificePerCell"/>.
    /// </summary>
    public int Sacrificed { get; init; }

    public override string ToString()
    {
        string what = Gained.Count == 0
            ? "ничего"
            : string.Join(", ", Gained.OrderByDescending(g => g.Value).Take(6)
                .Select(g => $"{g.Key} x{g.Value}"));
        return $"сломано {Broken} из {Planned} кл. (изолированных {Isolated}" +
               (AutoDropped > 0 ? $", осыпалось само {AutoDropped}" : "") +
               $"), пропущено {Skipped}" +
               (Skips.Count > 0
                   ? " (" + string.Join(", ", Skips.OrderByDescending(k => k.Value).Take(3)
                       .Select(k => $"{k.Key}: {k.Value}")) + ")"
                   : "") +
               (Sacrificed > 0 ? $"; отдано в жертву ради подхода {Sacrificed} бл" : "") +
               $"; получено: {what} " +
               $"(целых блоков {WholeGained}, крошки {RubbleGained}); " +
               $"прочности потрачено {DurabilitySpent}; " +
               $"{Seconds:0} с" +
               (Returns > 0 ? $"; возвращался после перерыва {Resume.Times(Returns)}" : "") +
               $"; остановка на {StoppedAt}: {Reason}";
    }
}

/// <summary>
/// СЧЁТ ЗА ПОДХОД: сколько блоков сломано ради того, чтобы дотянуться до
/// клетки, и к какой именно клетке.
///
/// ПОЧЕМУ ЭТО ОТДЕЛЬНАЯ ВЕЩЬ, А НЕ ПАРА ПОЛЕЙ В ХОДЕ РАБОТЫ. Счёт живёт дольше
/// одного захода к клетке: к одной и той же клетке карьер подступается до
/// четырёх раз (перед зовом дороги, после отказа «не дотянуться», и всё это
/// ещё раз на втором проходе по слою). Пока счётчик лежал внутри одного захода,
/// <see cref="Quarry.MaxSacrificePerCell"/> ограничивал НЕ ту клетку, а тот
/// заход: при пределе 2 одна клетка стоила до шести сломанных соседей, и потолок
/// ставила не настройка человека, а число граней у куба. Человек, урезавший цену
/// до двух, получал втрое больше — и видел в отчёте число, которого не заказывал.
///
/// Здесь же живёт и разбор, ЧЕМ заплачено. Своя же клетка «в крошку», сломанная
/// раньше срока, платой не считается: её и так ломать, это обычная работа, и
/// записать её надо в сделанное, иначе после перерыва бот пойдёт ломать то,
/// чего в мире уже нет. Жертва — это блок ЗА ГРАНИЦЕЙ плана.
/// </summary>
public sealed class SacrificeLedger
{
    private readonly Dictionary<BlockPos, int> заКлетку = [];

    /// <summary>Блоки за границей плана, сломанные ради подхода, — счёт человеку.</summary>
    public int Sacrificed { get; private set; }

    /// <summary>Свои же клетки плана, сломанные раньше срока ради подхода.</summary>
    public int Early { get; private set; }

    /// <summary>Сколько всего сломано ради подхода — по этому числу и считается предел.</summary>
    public int Paid => Sacrificed + Early;

    /// <summary>Сколько уже сломано ради подхода ИМЕННО К ЭТОЙ клетке.</summary>
    public int PaidFor(BlockPos cell) => заКлетку.GetValueOrDefault(cell);

    /// <summary>Осталось ли право платить за подход к этой клетке.</summary>
    public bool MayPayFor(BlockPos cell, int limit) => PaidFor(cell) < limit;

    /// <summary>
    /// Записать сломанного ради подхода соседа. <paramref name="ownPlanCell"/> —
    /// сосед стоял в плане карьера. Возвращает true, если это ЖЕРТВА, то есть
    /// счёт человеку вырос.
    /// </summary>
    public bool Note(BlockPos cell, bool ownPlanCell)
    {
        заКлетку[cell] = PaidFor(cell) + 1;
        if (ownPlanCell)
            Early++;
        else
            Sacrificed++;
        return !ownPlanCell;
    }
}

/// <summary>
/// Карьер: добыча камня «шахматкой».
///
/// ПОЧЕМУ ИМЕННО ШАХМАТКА. Обычный камень выпадает крошкой: rock.json,
/// dropsByType → item "stone-{rock}", 2.5±0.5 штуки. Но у блока первым в списке
/// стоит поведение BreakIfFloating, а оно перехватывает выдачу дропа:
/// BlockBehaviorBreakIfFloating.GetDrops (vssurv/Vintagestory.GameContent,
/// строки 38-52) при IsSurroundedByNonSolid возвращает
/// new ItemStack(block, 1) и ставит handled = PreventSubsequent, а
/// Block.GetDrops (pm/Block.cs:1308-1352) на PreventSubsequent сразу выходит,
/// не заглядывая в Drops. То есть изолированный блок падает ЦЕЛИКОМ.
/// Ровно это и написано в подсказке самого блока:
/// "Full block can be obtained by breaking all adjacent blocks".
///
/// ЗАКАЗЧИК ОШИБСЯ В ОДНОМ. Условие не «с 4 сторон», а со ВСЕХ ШЕСТИ:
/// IsSurroundedByNonSolid (строки 59-70) обходит BlockFacing.ALLFACES, то есть
/// север/восток/юг/запад/ВЕРХ/НИЗ. Значит плоская шахматка в одном слое целых
/// блоков не даёт вовсе — сверху и снизу остаётся камень. Работает только
/// ОБЪЁМНАЯ шахматка по чётности (X+Y+Z): сначала выбираются все клетки одной
/// чётности (они дают крошку), после чего у каждой клетки другой чётности все
/// шесть соседей пусты — и она выпадает целым блоком.
///
/// И ВТОРАЯ ОШИБКА — УЖЕ НАША. Мы посчитали, что так выйдет ПОЛОВИНА объёма
/// целыми: шахматка и правда наибольшее независимое множество в решётке с
/// шестью соседями. Но у этой половины есть цена, которой в счёте не было:
/// пустоты ровной шахматки не касаются друг друга гранями, а значит по ним
/// нельзя ни пройти, ни даже кликнуть — каждая клетка «в крошку» ниже верхнего
/// слоя замурована шестью «целыми». Живьём это выглядело так: «он сделал 2 слоя
/// и затупил». Поэтому каждый второй слой отдан целиком в жертву ради подхода
/// (<see cref="IsHarvestLayer"/>), и целыми выходит ЧЕТВЕРТЬ объёма. Заказчик
/// сказал это раньше нас: «шахматной сеткой идеально копать нельзя… придётся
/// принести какие-то в жертву, чтобы дотянуться».
///
/// «Твёрдость» соседа считается ровно так же, как её считает сервер:
/// BlockAccessorBase.IsSideSolid (vslib/Vintagestory.Common) смотрит сперва
/// слой жидкостей (лёд твёрдый, вода — нет), потом слой блоков, и берёт
/// Block.SideSolid[грань]. Поэтому вода и воздух рядом изоляции НЕ мешают.
///
/// Класс — механизм: размер области, что делать с крошкой, глубина, пандус,
/// пороги износа и осторожности задаются настройками, а не зашиты.
/// </summary>
public class Quarry
{
    private readonly BotContext ctx;

    public Quarry(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    // ---------------- настройки ----------------

    /// <summary>
    /// КАК СНИМАТЬ ОБЪЁМ: шахматкой (дешевле по добыче) или целиком (ровная
    /// яма). Разница подробно — в <see cref="QuarryCut"/>.
    ///
    /// ЗАЧЕМ ЭТА РУЧКА ПОЯВИЛАСЬ. Карьер с самого начала копал только
    /// шахматкой: ради целых блоков её и затевали. Заказчик же дал команду
    /// «карьер» и ждал РОВНУЮ ЯМУ, а увидел решётку недобранных клеток и
    /// решил, что бот не доделал работу. Оба ожидания законные — значит это
    /// выбор человека, а не догадка механизма.
    ///
    /// По умолчанию шахматка: так карьер работал до этой правки.
    /// </summary>
    public QuarryCut Cut { get; set; } = QuarryCut.Checkerboard;

    /// <summary>
    /// Какую чётность (X+Y+Z)&amp;1 собирать целыми блоками. null — выбрать ту,
    /// что даёт больше целых блоков в конкретной области. К
    /// <see cref="QuarryCut.Whole"/> отношения не имеет: там целых блоков нет.
    /// </summary>
    public int? HarvestParity { get; set; }

    /// <summary>
    /// Оставлять по краю области винтовой пандус из неразрушенных ступеней.
    /// Это и есть защита от «выкопал яму и не вылез»: без него бот, сняв
    /// N слоёв, оказывается на дне стены высотой N, а прыжок берёт только 1.
    /// Пандус стоит нескольких целых блоков (его соседи не изолируются) —
    /// поэтому это настройка, а не закон.
    /// </summary>
    public bool KeepExitRamp { get; set; } = true;

    /// <summary>
    /// Считать силы на подъём ДО первого удара киркой.
    ///
    /// Карьер — это яма, которую бот роет под собой: с каждым снятым слоем
    /// край уходит выше ещё на блок, а прыжок берёт ровно один. Отказ
    /// «из ямы не выбраться», сказанный на дне, уже ничего не меняет —
    /// поэтому считаем заранее и отказываемся, пока яма не вырыта.
    /// </summary>
    public bool CheckEscapeBeforeStart { get; set; } = true;

    /// <summary>
    /// Строить выход, когда ногами не выйти: лестница по стене карьера, а
    /// если лестниц нет — столб под собой. Механика чужая
    /// (<see cref="Scaffolding.GetOutAsync"/>), здесь только разрешение.
    /// Выключают там, где столб посреди карьера мешает больше, чем застрявший
    /// бот, — но тогда пандус обязателен.
    /// </summary>
    public bool MayClimbOut { get; set; } = true;

    /// <summary>Сколько секунд отводим на выход наверх после работы.</summary>
    public double EscapeSeconds { get; set; } = 120;

    /// <summary>
    /// Ломать клетку прямо под собой и падать в неё — как делает игрок.
    /// Выключать это стоит только там, где падение опасно само по себе
    /// (навесная работа над обрывом, разбор потолка).
    /// </summary>
    public bool DigUnderSelf { get; set; } = true;

    /// <summary>
    /// На сколько блоков боту позволено уронить себя, ломая пол под ногами.
    /// Игра начинает бить за падение примерно с 3,5 блоков, поэтому три —
    /// это «без урона», а не «как повезёт».
    /// </summary>
    public int MaxSelfDrop { get; set; } = 3;

    /// <summary>Подбирать крошку (stone-…) после каждого слоя.</summary>
    public bool CollectRubble { get; set; } = true;

    /// <summary>Подбирать целые блоки после сбора.</summary>
    public bool CollectWhole { get; set; } = true;

    /// <summary>Радиус подбора выпавшего, блоков.</summary>
    public double CollectRadius { get; set; } = 6;

    /// <summary>Сколько секунд тратить на подбор за один заход.</summary>
    public double CollectSeconds { get; set; } = 15;

    /// <summary>Увидев лаву или кипяток вплотную к клетке — бросать всю работу.</summary>
    public bool StopOnLava { get; set; } = true;

    /// <summary>Не вскрывать клетки, к которым примыкает вода (иначе карьер зальёт).</summary>
    public bool AvoidWater { get; set; } = true;

    /// <summary>
    /// Ниже этого остатка прочности кирку менять; если менять не на что —
    /// останавливаться, а не добивать инструмент до поломки.
    /// </summary>
    public int MinToolDurability { get; set; } = 25;

    /// <summary>Останавливаться, когда в сумке не осталось свободных слотов.</summary>
    public bool StopWhenFull { get; set; } = true;

    /// <summary>
    /// Потолок по времени НА ВЕСЬ КАРЬЕР, секунд, — и слово «весь» тут не
    /// украшение, а закон работы: срока на ЗАХОД у карьера нет вовсе. Разбор,
    /// почему так и чем за это плачено, — у
    /// <see cref="KeepQuarryingAsync(QuarryPlan, Resume, BodyArbiter.Importance,
    /// CancellationToken)"/>.
    /// </summary>
    public double MaxSeconds { get; set; } = 900;

    /// <summary>
    /// ЧАСЫ КАРЬЕРА — ОДИН ВХОД НА ВЕСЬ КЛАСС, И ОН ЖЕ ШОВ ДЛЯ СТЕНДА.
    ///
    /// ЗАЧЕМ ШОВ. Срок карьера живьём стенной, и это правильно: человек сказал
    /// «четверть часа» про свои часы, а не про число клеток. А вот СТЕНД,
    /// меряющий срок стенными часами, сам становится плавающим — и это в
    /// проекте уже трижды находили под именем «СТЕННЫЕ ЧАСЫ ВМЕСТО ЧАСОВ ТЕЛА»
    /// (<c>Movement.BodySeconds</c>, <c>Body</c>). Живой замер приёмки: стенд
    /// <c>КарьерУВодыДокладываетСрокTests</c> брал срок как ЧЕТВЕРТЬ
    /// измеренного полного захода — и краснел 1 полный прогон из 7 под
    /// нагрузкой, потому что само измерение зависело от загрузки машины.
    ///
    /// ПОЧЕМУ ЭТО НЕ ПОДМЕНА ЖИВОГО ПУТИ. Спрашивается время РОВНО ОДИН РАЗ НА
    /// КЛЕТКУ (<c>MineCellAsync</c>), значит часы, идущие по вопросам, идут по
    /// РАБОТЕ — то самое «часы тела». Подменив их, стенд получает срок,
    /// который наступает на заранее известной клетке, а весь остальной путь —
    /// пропуск, перевод остановки в общее слово, правило пережитой помехи —
    /// остаётся боевым.
    ///
    /// Образец не свой: ровно так же и теми же словами открыты часы у
    /// <c>Pickup</c>, <c>TranslocatorMemory</c>, <c>Translocators</c>.
    /// </summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Следить за нестабильностью породы над головой.</summary>
    public bool CareAboutCeiling { get; set; } = true;

    /// <summary>
    /// Допустимая нестабильность потолка над головой, 0..1. В игре обвал
    /// случается, когда rand &lt; instability И rand &lt; collapseChance
    /// (BlockBehaviorUnstableRock.CheckCollapsible), то есть при 1.0 шанс равен
    /// collapseChance (0.2…0.5 по типу камня). 0.6 — заметный запас.
    /// </summary>
    public double MaxInstability { get; set; } = 0.6;

    /// <summary>
    /// Запасное значение maxSupportDistance, если его не удалось прочесть
    /// из свойств поведения UnstableRock. 2 — самый строгий вариант из rock.json
    /// (глины, песчаники, известняки).
    /// </summary>
    public double DefaultMaxSupportDistance { get; set; } = 2;

    // ---------------- правила игры о твёрдости и изоляции ----------------
    //
    // ЗДЕСЬ ИХ БОЛЬШЕ НЕТ, И ЭТО НАРОЧНО. Имя поведения игры, копия
    // IsSideSolid и копия IsSurroundedByNonSolid переехали в <see
    // cref="WholeStone"/> целиком — потому что тот же вопрос («что выпадет с
    // каменного блока и от чего это зависит») задаёт ВТОРАЯ работа: поручение
    // «добудь камень» освобождает шесть граней одной клетки, а карьер
    // раскладывает шахматкой целый объём. Оставь правило внутри карьера — и у
    // поручения завелась бы копия, а копии правил о выпадениях в этом проекте
    // расходились уже пять раз.

    /// <summary>
    /// Твёрдая ли грань соседа. Ответ один на весь проект и живёт у цельного
    /// камня (<see cref="WholeStone.SideSolidAt"/>); здесь только адрес мира.
    /// </summary>
    public bool SideSolidAt(int x, int y, int z, int faceIndex) =>
        WholeStone.SideSolidAt(ctx.World, x, y, z, faceIndex);

    /// <summary>
    /// Ни одного твёрдого соседа по всем шести граням — условие игры, при
    /// котором блок выпадает целиком (<see cref="WholeStone.Floats(WorldModel, BlockPos)"/>).
    /// </summary>
    public bool IsFloating(BlockPos pos) => WholeStone.Floats(ctx.World, pos);

    /// <summary>
    /// ВЫПАДЕТ ЛИ ЭТОТ БЛОК ЦЕЛЫМ, ЕСЛИ ЕГО ИЗОЛИРОВАТЬ — СПРОШЕНО У РЕЕСТРА
    /// (<see cref="WholeStone.DropsWholeWhenFloating(WorldModel, int)"/>, там же
    /// и разбор живого случая про рукописный список приставок).
    /// </summary>
    public bool DropsWholeWhenFloating(int blockId) =>
        WholeStone.DropsWholeWhenFloating(ctx.World, blockId);

    /// <summary>То же по коду блока — нужно, чтобы разобрать прирост в сумке.</summary>
    public bool DropsWholeWhenFloating(string code) =>
        WholeStone.DropsWholeWhenFloating(ctx.World, code);

    // ---------------- обвалы: нестабильность потолка ----------------

    /// <summary>
    /// Сила вертикальной опоры под клеткой — копия
    /// BlockBehaviorUnstableRock.getVerticalSupportStrength: смотрим 4 блока вниз,
    /// балка с атрибутом unstableRockStabilization даёт свою силу, первый
    /// «не твёрдый сверху и снизу» блок обнуляет опору.
    /// </summary>
    public int VerticalSupportStrength(BlockPos pos)
    {
        if (!ctx.World.GameBlocksReady)
            return 1;   // не знаем — считаем, что опора есть: тревогу зря не поднимаем
        for (int i = 1; i < 5; i++)
        {
            int y = Math.Max(0, pos.Y - i);
            var b = ctx.World.GetGameBlock(pos.X, y, pos.Z);
            int stabilization = b.Attributes?["unstableRockStabilization"].AsInt(0) ?? 0;
            if (stabilization > 0)
                return stabilization;
            if (!b.SideSolid[Facing.UP.Index] || !b.SideSolid[Facing.DOWN.Index])
                return 0;
        }
        return 1;
    }

    /// <summary>Квадрат горизонтального расстояния между клетками.</summary>
    private static double HorDistSq(int x, int z, BlockPos to) =>
        (double)(x - to.X) * (x - to.X) + (double)(z - to.Z) * (z - to.Z);

    /// <summary>
    /// Расстояние до ближайшей вертикальной опоры — копия
    /// getNearestVerticalSupports: волна расходится по ГОРИЗОНТАЛЯМ через блоки,
    /// твёрдые с обеих сторон по ходу волны, дальше 6 блоков (36 в квадрате)
    /// поиск не идёт. Бесконечность — опоры нет вовсе.
    /// </summary>
    public double NearestSupportDistance(BlockPos start)
    {
        if (VerticalSupportStrength(start) > 0)
            return 0;

        var supports = new List<(int X, int Z, int W)>();
        var queue = new Queue<BlockPos>();
        var seen = new HashSet<BlockPos>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!seen.Add(p))
                continue;
            for (int i = 0; i < Facing.HORIZONTALS.Length; i++)
            {
                var f = Facing.HORIZONTALS[i];
                var n = new BlockPos(p.X + f.Normali.X, p.Y, p.Z + f.Normali.Z);
                // maxSupportSearchDistanceSq = 36 в BlockBehaviorUnstableRock
                if (HorDistSq(n.X, n.Z, start) > 36)
                    continue;
                if (!SideSolidAt(n.X, n.Y, n.Z, f.Index) ||
                    !SideSolidAt(n.X, n.Y, n.Z, f.Opposite.Index))
                    continue;
                int w = VerticalSupportStrength(n);
                if (w > 0)
                    supports.Add((n.X, n.Z, w));
                else
                    queue.Enqueue(n);
            }
        }

        if (supports.Count == 0)
            return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        foreach (var s in supports)
            best = Math.Min(best, Math.Sqrt(Math.Max(0, HorDistSq(s.X, s.Z, start) - (s.W - 1))));
        return best;
    }

    private readonly Dictionary<int, double> supportDistanceCache = new();

    /// <summary>
    /// maxSupportDistance из свойств поведения UnstableRock; -1 — такого поведения
    /// у блока нет, обваливаться он не умеет.
    ///
    /// Свойства настоящие, серверные: реестр подменяет незнакомый КЛАСС поведения
    /// заглушкой, но CollectibleBehavior.Initialize всё равно кладёт присланный
    /// JSON в propertiesAtString. Там лежит ровно то, что в rock.json:
    /// 2 у глин, песчаников и известняков, 4 у сланцев, 6 у остального камня.
    /// </summary>
    private double ReadMaxSupportDistance(int blockId)
    {
        if (supportDistanceCache.TryGetValue(blockId, out double cached))
            return cached;

        double result = -1;
        var behaviors = ctx.World.GetGameBlockById(blockId)?.CollectibleBehaviors;
        foreach (var b in behaviors ?? Array.Empty<Vintagestory.API.Common.CollectibleBehavior>())
        {
            if (b.propertiesAtString is not { Length: > 2 } json ||
                !json.Contains("maxSupportDistance", StringComparison.Ordinal))
                continue;
            try
            {
                var token = Newtonsoft.Json.Linq.JObject.Parse(json)["maxSupportDistance"];
                double d = token == null ? 0 : token.ToObject<double>();
                if (d > 0)
                {
                    result = d;
                    break;
                }
            }
            catch { /* свойства пришли не JSON-ом — считаем, что поведения нет */ }
        }
        supportDistanceCache[blockId] = result;
        return result;
    }

    /// <summary>Расстояние, с которого камень этого типа держится за опору.</summary>
    public double MaxSupportDistanceOf(BlockPos pos)
    {
        double d = ReadMaxSupportDistance(ctx.World.GetBlockId(pos.X, pos.Y, pos.Z));
        return d > 0 ? d : DefaultMaxSupportDistance;
    }

    /// <summary>
    /// Умеет ли порода в клетке обваливаться (поведение UnstableRock).
    /// Если свойств поведения прочесть не удалось, спрашиваем реестр про
    /// «выпадает целым»: в игре UnstableRock висит на тех же блоках, что и
    /// BreakIfFloating. Раньше вторым вопросом был список приставок, и он
    /// молча терял всё, что в него не попало.
    /// </summary>
    public bool IsUnstableRock(BlockPos pos)
    {
        int id = ctx.World.GetBlockId(pos.X, pos.Y, pos.Z);
        if (id == 0)
            return false;
        return ReadMaxSupportDistance(id) > 0 || DropsWholeWhenFloating(id);
    }

    /// <summary>
    /// Нестабильность клетки 0..1+ — как её показывает сама игра в подсказке
    /// блока (getInstability): расстояние до опоры, делённое на maxSupportDistance.
    /// </summary>
    public double Instability(BlockPos pos)
    {
        double max = MaxSupportDistanceOf(pos);
        if (max <= 0)
            return 0;
        double d = NearestSupportDistance(pos);
        return double.IsInfinity(d) ? 99 : Math.Clamp(d / max, 0, 99);
    }

    // ---------------- досягаемость: есть ли откуда дотянуться ----------------
    //
    // ГЛАВНОЕ ПРАВИЛО КАРЬЕРА: ПОРЯДОК КОПКИ ОБЯЗАН ГАРАНТИРОВАТЬ ДОСЯГАЕМОСТЬ.
    //
    // ЖИВОЙ СЛУЧАЙ, ради которого написан весь этот раздел. Карьер 7×15: бот
    // стоял на оставшихся столбах решётки и десятки раз в секунду повторял
    //   «до (512029, 108, 512218) не хватает 2 бл по высоте, и ближе не
    //    становится — обычной дорогой отсюда не выйти»
    //   «не дойти: наверх — я внутри своей работы».
    // Отказ был ПРАВИЛЬНЫЙ, мы сами его и вводили, но лечил он следствие.
    // Причина в другом: план выдавал клетку, к которой ПОДОЙТИ НЕЧЕМ, и звал
    // дорогу к тому, куда дороги нет и быть не может.
    //
    // Заказчик описал это раньше нас, слово в слово: «шахматной сеткой
    // идеально копать нельзя, потому что блоки падают только если убрать с
    // 4 сторон блок, но тогда придётся принести какие-то в жертву, чтобы
    // дотянуться». Отсюда два правила ниже: ЧЕМ ДОТЯНУТЬСЯ (рука и тело) и
    // ЧТО СНЯТЬ, если дотянуться нечем (жертва).

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО РУКИ: дотянется ли тело от ног в <paramref name="stand"/>
    /// до середины клетки. Считаем от ГЛАЗ до центра клетки — ровно ту же
    /// величину проверяет сервер.
    ///
    /// Длину руки и высоту глаз не выдумываем, а спрашиваем у
    /// <see cref="Hands"/>: там это одна правда на весь проект (рука —
    /// PickingRange игры, 4.5 бл; глаза — 1.7 из player.json). Сюда они
    /// приходят числами затем, чтобы правило можно было проверить без тела,
    /// без сервера и без мира.
    /// </summary>
    public static bool ArmReaches(BlockPos stand, BlockPos cell, double eyeHeight, double reach) =>
        Distance(stand.X + 0.5, stand.Y + eyeHeight, stand.Z + 0.5,
                 cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5) <= reach;

    /// <summary>
    /// Открыта ли к этому месту хоть одна грань клетки.
    ///
    /// Ломают в игре не «блок», а ГРАНЬ, и видна она только с той стороны,
    /// куда смотрит. Значит блок, замурованный соседями со всех шести сторон,
    /// не сломать ВОВСЕ — хоть встань вплотную. Ровно это и происходило с
    /// ровной шахматкой: у неё каждая клетка «в крошку» ниже верхнего слоя
    /// окружена шестью «целыми».
    /// </summary>
    public bool FaceOpenTowards(BlockPos cell, BlockPos stand)
    {
        // Из ГЛАЗ, а не из ног: грань, которая смотрит вниз, боту с пола не
        // видна, и клик по ней сервер не примет
        double ex = stand.X - cell.X;
        double ey = stand.Y + ctx.Hands.EyeHeight - (cell.Y + 0.5);
        double ez = stand.Z - cell.Z;
        for (int i = 0; i < Facing.ALLFACES.Length; i++)
        {
            var f = Facing.ALLFACES[i];
            if (!ctx.World.IsPassable(cell.X + f.Normali.X, cell.Y + f.Normali.Y,
                    cell.Z + f.Normali.Z))
                continue;   // эту грань закрывает сосед
            if (ex * f.Normali.X + ey * f.Normali.Y + ez * f.Normali.Z > 0)
                return true;
        }
        return false;
    }

    /// <summary>Хоть одна грань клетки открыта — иначе её не сломать ниоткуда.</summary>
    public bool HasOpenFace(BlockPos cell)
    {
        for (int i = 0; i < Facing.ALLFACES.Length; i++)
        {
            var f = Facing.ALLFACES[i];
            if (ctx.World.IsPassable(cell.X + f.Normali.X, cell.Y + f.Normali.Y,
                    cell.Z + f.Normali.Z))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Годится ли это место, чтобы бить по клетке. Три условия, и все три —
    /// про живое тело, а не про геометрию точек:
    ///   1. ТЕЛО ВЛЕЗАЕТ. Рост игрока 1.85 (<see cref="Walker.BodyHeight"/>),
    ///      то есть под ним заняты ДВЕ клетки — ноги и голова, — и нужен пол.
    ///      Всё это уже считает мир (WorldModel.IsStandable), второй такой
    ///      механики заводить нельзя.
    ///   2. РУКА ДОСТАЁТ (<see cref="ArmReaches"/>).
    ///   3. ГРАНЬ ВИДНА С ЭТОЙ СТОРОНЫ (<see cref="FaceOpenTowards"/>).
    /// </summary>
    public bool CanWorkFrom(BlockPos stand, BlockPos cell)
    {
        // В саму клетку и в ту, что над ней, телом не встать: там ещё камень
        if (stand == cell || new BlockPos(stand.X, stand.Y + 1, stand.Z) == cell)
            return false;

        // СТОЯ НА КЛЕТКЕ её тоже ломают — так делает живой игрок, копая вниз.
        // Но только если это разрешено и падать недалеко: иначе «место есть»
        // означало бы «прыгни в шахту»
        if (stand == new BlockPos(cell.X, cell.Y + 1, cell.Z) &&
            !(DigUnderSelf && SafeToDropInto(cell)))
            return false;

        return ctx.World.IsStandable(stand.X, stand.Y, stand.Z) &&
               ArmReaches(stand, cell, ctx.Hands.EyeHeight, ctx.Hands.Reach) &&
               (!CareAboutCeiling || CeilingSafeAt(stand)) &&
               FaceOpenTowards(cell, stand);
    }

    /// <summary>
    /// Места, откуда до клетки дотянется тело, — ближайшие к ногам первыми.
    /// Пусто — подойти нечем, и звать дорогу незачем: именно этот зряшный зов
    /// и превращался в «не хватает 2 бл по высоте» десятки раз в секунду.
    /// </summary>
    public IEnumerable<BlockPos> StandSpotsFor(BlockPos cell, int radius = 3)
    {
        // Замурованную клетку не сломать НИОТКУДА — обходить ради неё сотню
        // мест незачем, ответ известен заранее
        if (!HasOpenFace(cell))
            return [];

        var from = Feet ?? cell;
        var found = new List<(BlockPos Spot, double Cost)>();
        // Выше клетки на два (стоя сверху) и ниже на два (стоя в яме рядом) —
        // дальше рука до середины клетки уже не достаёт
        for (int dy = 2; dy >= -2; dy--)
            for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    var spot = new BlockPos(cell.X + dx, cell.Y + dy, cell.Z + dz);
                    if (CanWorkFrom(spot, cell))
                        found.Add((spot,
                            Distance(from.X, from.Y, from.Z, spot.X, spot.Y, spot.Z)));
                }
        return found.OrderBy(t => t.Cost).Select(t => t.Spot);
    }

    /// <summary>Ближайшее место, откуда до клетки дотянуться. null — нечем.</summary>
    public BlockPos? StandSpotFor(BlockPos cell, int radius = 3)
    {
        foreach (var spot in StandSpotsFor(cell, radius))
            return spot;
        return null;
    }

    /// <summary>
    /// ЕСТЬ ЛИ ВООБЩЕ ПОДХОД К КЛЕТКЕ: либо мы уже дотягиваемся, либо есть
    /// куда встать. Это и есть та проверка, которой плану не хватало.
    /// </summary>
    public bool CanBeReached(BlockPos cell) =>
        (Feet is not null && ctx.Hands.InReach(cell)) || StandSpotFor(cell) is not null;

    // ---------------- план ----------------

    /// <summary>
    /// СЛОЙ-ЖЕРТВА КАРЬЕРА: каждый второй слой снимается ЦЕЛИКОМ, и целых
    /// блоков в нём не берут вовсе. Считаем от верха области: верхний слой —
    /// добычный.
    ///
    /// ЗДЕСЬ ТОЛЬКО АДРЕС, А ПРАВИЛО ЛЕЖИТ У ШАХМАТКИ
    /// (<see cref="WholeStone.ДобычныйСлой"/>) — вместе с разбором, почему без
    /// слоёв-жертв карьер «сделал 2 слоя и затупил». Карьер добавляет к правилу
    /// ровно одно своё слово: ЕГО забой горизонтален, работа уходит вниз, ось —
    /// Y.
    ///
    /// ШТОЛЬНЯ В ГОРЕ ШАХМАТКУ НЕ ЗОВЁТ ВОВСЕ, и здесь стояла обратная фраза —
    /// снята 03.09. Ход выгрызается ОДНИМ СРЕЗОМ за шаг, у всех клеток среза
    /// координата вдоль хода одна, чередовать нечего, а впереди стоит нетронутый
    /// камень — целых блоков выходит ноль при любой ширине (живая проба:
    /// 3×3×8, семьдесят две клетки, ноль целых). Правило от этого всё равно
    /// одно и лежит у <see cref="WholeStone"/>: второй копии ради своего случая
    /// заводить нельзя (закон 2).
    /// </summary>
    public static bool IsHarvestLayer(int y, int topY) =>
        WholeStone.ДобычныйСлой(new BlockPos(0, y, 0), ЗабойЯмы(topY));

    /// <summary>
    /// ЗАБОЙ КАРЬЕРА: горизонтальная площадка, работа уходит ВНИЗ. Начало —
    /// верхний слой области: с него карьер и начинает.
    /// </summary>
    private static Забой ЗабойЯмы(int topY) =>
        new(Vintagestory.API.MathTools.EnumAxis.Y, topY);

    /// <summary>Клетки по периметру основания области, подряд — соседние друг другу.</summary>
    private static List<(int X, int Z)> Perimeter(BlockPos lo, BlockPos hi)
    {
        var ring = new List<(int, int)>();
        for (int x = lo.X; x <= hi.X; x++) ring.Add((x, lo.Z));
        for (int z = lo.Z + 1; z <= hi.Z; z++) ring.Add((hi.X, z));
        for (int x = hi.X - 1; x >= lo.X; x--) ring.Add((x, hi.Z));
        for (int z = hi.Z - 1; z > lo.Z; z--) ring.Add((lo.X, z));
        return ring;
    }

    /// <summary>
    /// Ступени винтового пандуса: по одной на слой, соседние по горизонтали.
    /// Бот сходит с уступа на уступ по одному блоку — и так же поднимается
    /// обратно, потому что прыжок в игре берёт ровно один блок.
    /// </summary>
    public HashSet<BlockPos> BuildRamp(BlockPos lo, BlockPos hi)
    {
        var ramp = new HashSet<BlockPos>();
        var ring = Perimeter(lo, hi);
        // Кольцо короче трёх клеток — это не пандус, а столб: в такой области
        // всё равно негде развернуться
        if (ring.Count < 3)
            return ramp;
        int i = 0;
        for (int y = hi.Y; y >= lo.Y; y--, i++)
        {
            var (x, z) = ring[i % ring.Count];
            ramp.Add(new BlockPos(x, y, z));
        }
        return ramp;
    }

    /// <summary>
    /// Разложить область на шахматку. Клетки выбранной чётности, которые удастся
    /// изолировать, идут в Harvest (целые блоки), всё остальное — в Clear
    /// (крошка). Чётность считается по X+Y+Z: в трёхмерной шахматке у клетки
    /// одной чётности все шесть соседей — другой.
    ///
    /// САМОГО ДЕЛЕНИЯ ЗДЕСЬ БОЛЬШЕ НЕТ, И ЭТО ГЛАВНОЕ В ЭТОМ МЕТОДЕ. Порядок
    /// «через одну» держит <see cref="WholeStone.ЧерезОдну"/> — одно правило на
    /// весь проект, с забоем в виде довода. Карьер приносит сюда только своё:
    /// область, пандус, ось забоя (вниз) и мерку «этот материал стоит брать
    /// целым». Пока правило жило здесь, дорога и штольня не имели к нему
    /// доступа вовсе, и заказчик получал крошку везде, кроме ямы.
    /// </summary>
    public QuarryPlan PlanCheckerboard(BlockPos a, BlockPos b, int? parity = null)
    {
        var lo = new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        var hi = new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        var ramp = KeepExitRamp ? BuildRamp(lo, hi) : new HashSet<BlockPos>();

        // ЧТО ВООБЩЕ СНИМАЕМ: вся область, кроме ступеней пандуса. Порядок
        // обхода — сверху вниз, как копает сам карьер: шахматка его сохранит
        // дословно внутри каждой половины
        var все = new List<BlockPos>();
        for (int y = hi.Y; y >= lo.Y; y--)
            for (int x = lo.X; x <= hi.X; x++)
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    var c = new BlockPos(x, y, z);
                    if (!ramp.Contains(c))
                        все.Add(c);
                }

        var забой = ЗабойЯмы(hi.Y);
        int chosen = parity ?? HarvestParity ?? WholeStone.ЛучшаяЧётность(все, забой, Твёрдо);

        // ШАХМАТКА НУЖНА ТОЛЬКО КАМНЮ. Целым блоком берётся лишь порода: у неё
        // выпадение зависит от того, касается ли блок соседей. Земля, песок и
        // глина падают целиком в любом случае — городить ради них шахматку
        // значит копать вдвое дольше и оставлять решётку, по которой не пройти
        var (clear, harvest) =
            WholeStone.ЧерезОдну(все, забой, chosen, WantsCheckerboard, Твёрдо);

        return new QuarryPlan
        {
            Min = lo, Max = hi, Parity = chosen,
            Clear = clear, Harvest = harvest, Ramp = ramp,
            Cut = QuarryCut.Checkerboard
        };
    }

    /// <summary>
    /// Упирается ли грань в твёрдую сторону соседа СЕЙЧАС — тот довод, который
    /// шахматка спрашивает у мира. Клетки внутри области она не спрашивает
    /// вовсе: те есть в списке, значит уйдут сами. Ступень пандуса в списке НЕ
    /// лежит — и оттого честно отвечает «твёрдо», как ей и положено: её не
    /// тронут никогда.
    /// </summary>
    private bool Твёрдо(BlockPos n, Facing f) => SideSolidAt(n.X, n.Y, n.Z, f.Opposite.Index);

    /// <summary>Область по центру верхнего слоя: сторона size, глубина depth вниз.</summary>
    public QuarryPlan PlanCheckerboard(BlockPos topCentre, int size, int depth, int? parity = null)
    {
        var (lo, hi) = BoxAround(topCentre, size, depth);
        return PlanCheckerboard(lo, hi, parity);
    }

    /// <summary>
    /// СНЯТЬ ОБЪЁМ ЦЕЛИКОМ: все клетки области подряд, слой за слоем, ничего не
    /// оставляя. Целых блоков тут нет вовсе — камень с соседями выпадает
    /// крошкой, — и это не недоработка, а цена ровной ямы (см.
    /// <see cref="QuarryCut.Whole"/>).
    ///
    /// Пандус берегут ТАК ЖЕ, как и при шахматке: он не про добычу, а про
    /// выход, и без него ровная яма — это ровная ловушка.
    /// </summary>
    public QuarryPlan PlanWholeVolume(BlockPos a, BlockPos b)
    {
        var lo = new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        var hi = new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        var ramp = KeepExitRamp ? BuildRamp(lo, hi) : new HashSet<BlockPos>();

        var clear = new List<BlockPos>();
        for (int y = hi.Y; y >= lo.Y; y--)
            for (int x = lo.X; x <= hi.X; x++)
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    var c = new BlockPos(x, y, z);
                    if (!ramp.Contains(c))
                        clear.Add(c);
                }

        return new QuarryPlan
        {
            // ЧЁТНОСТЬ ТУТ НИ ПРИ ЧЁМ: целых клеток нет, делить объём не на
            // что. Поле в плане обязательное, поэтому пишем то, что попросили
            // настройкой (или ноль) — и НЕ считаем лучшую чётность: этот
            // счёт обходит весь объём ради числа, которым никто не пользуется
            Min = lo, Max = hi, Parity = HarvestParity ?? 0,
            Clear = clear, Harvest = [], Ramp = ramp,
            Cut = QuarryCut.Whole
        };
    }

    /// <summary>Та же область, снятая целиком: сторона size, глубина depth вниз.</summary>
    public QuarryPlan PlanWholeVolume(BlockPos topCentre, int size, int depth)
    {
        var (lo, hi) = BoxAround(topCentre, size, depth);
        return PlanWholeVolume(lo, hi);
    }

    /// <summary>
    /// ПЛАН ПО НАСТРОЙКЕ РОЛИ (<see cref="Cut"/>) — то, что зовут команды.
    /// Прямые PlanCheckerboard и PlanWholeVolume остаются для тех, кто хочет
    /// определённого способа независимо от настройки.
    /// </summary>
    public QuarryPlan Plan(BlockPos a, BlockPos b) =>
        РовнойЯмой ? PlanWholeVolume(a, b) : PlanCheckerboard(a, b);

    /// <summary>План по настройке роли для области вокруг центра верхнего слоя.</summary>
    public QuarryPlan Plan(BlockPos topCentre, int size, int depth) =>
        РовнойЯмой
            ? PlanWholeVolume(topCentre, size, depth)
            : PlanCheckerboard(topCentre, size, depth);

    /// <summary>
    /// СНИМАТЬ ОБЪЁМ ПОДРЯД, БЕЗ ШАХМАТКИ, — и причин к этому ДВЕ, разных.
    ///
    /// Первая — <see cref="Cut"/>, ручка «РовнаяЯмаЦеликом»: человеку нужна яма
    /// (котлован, площадка, ров), а не камень, и решётка недобранных клеток на
    /// снимке читается как недоделанная работа. Это про ВИД.
    ///
    /// Вторая — общий выключатель шахматки у самого удара
    /// (<see cref="Mining.ЧерезОдну"/>, ручка «ШахматкойРадиЦельныхБлоков»).
    /// Она стоит у ВСЕХ живущих ролей и говорит «камень через одну не копать»;
    /// не спроси её карьер — человек выключил бы шахматку в окне, увидел бы её
    /// в яме и справедливо решил, что рычаг нарисованный.
    ///
    /// СЛОЖЕНЫ ОНИ ЗДЕСЬ, В ОДНОМ ВЫРАЖЕНИИ, а не разнесены по двум веткам:
    /// разойдись этот вопрос копиями, «карьер 7 15» означал бы разное в
    /// зависимости от того, каким путём его позвали, — на этом карьер уже
    /// горел (см. <see cref="TopCentreUnder"/>).
    /// </summary>
    private bool РовнойЯмой => Cut == QuarryCut.Whole || !ctx.Mining.ЧерезОдну;

    /// <summary>
    /// Углы области по центру верхнего слоя. Считается в одном месте: способов
    /// снятия объёма два, а область у них одна и та же, и разойдись этот счёт
    /// копиями — «карьер 7 15» означал бы разное в зависимости от настройки.
    /// </summary>
    private static (BlockPos Lo, BlockPos Hi) BoxAround(BlockPos topCentre, int size, int depth)
    {
        int half = (size - 1) / 2;
        var lo = new BlockPos(topCentre.X - half, topCentre.Y - depth + 1, topCentre.Z - half);
        return (lo, new BlockPos(lo.X + size - 1, topCentre.Y, lo.Z + size - 1));
    }

    /// <summary>
    /// Центр верхнего слоя карьера для того, кто стоит в точке (x, y, z).
    ///
    /// ЭТО БЛОК ПОД НОГАМИ, А НЕ КЛЕТКА НОГ. <see cref="BoxAround"/> ставит
    /// верхнюю границу области ровно в переданный Y, а клетка ног — это воздух,
    /// в котором бот стоит. Назови центром её — и верхний слой плана окажется
    /// пустым: «!карьер 7 15» пообещал бы 735 клеток, а породы снял бы 686, и
    /// сорок девять клеток воздуха ушли бы в ветку «тут пусто» — ни в сломанные,
    /// ни в пропущенные. Отчёт читался бы как недоделанная работа.
    ///
    /// Правило стоит здесь одно на всех, кто зовёт карьер «отсюда»: команда
    /// чата, навык роли и инструмент нейросети считали его копиями, и одна из
    /// трёх копий копала на слой выше остальных — одна мерка давала разный
    /// результат в зависимости от того, каким путём её задали.
    /// </summary>
    public static BlockPos TopCentreUnder(double x, double y, double z) =>
        new((int)Math.Floor(x), (int)Math.Floor(y) - 1, (int)Math.Floor(z));

    /// <summary>Центр верхнего слоя карьера под ногами бота — если он знает, где стоит.</summary>
    public BlockPos? TopCentreUnderSelf =>
        ctx.Self.Position is { } p ? TopCentreUnder(p.X, p.Y, p.Z) : null;

    /// <summary>
    /// Материалы, которые берут шахматкой. Смысл шахматки — получить ЦЕЛЫЙ
    /// блок вместо крошки, а это свойство есть только у породы: камень,
    /// добытый в отрыве от соседей, выпадает камнем, а не булыжником.
    /// Земле, песку и глине шахматка не нужна — они и так падают целиком.
    /// </summary>
    public HashSet<Material> CheckerboardMaterials { get; } =
    [
        Material.Stone,
        Material.Ore,
        Material.Ceramic
    ];

    /// <summary>Брать ли эту клетку шахматкой (иначе — сплошняком).</summary>
    public bool WantsCheckerboard(BlockPos cell) =>
        CheckerboardMaterials.Contains(ctx.World.BlockMaterial(cell.X, cell.Y, cell.Z));

    // Своего счёта чётностей у карьера больше нет: он был третьей копией
    // одного и того же перебора («слой добычный, соседи уйдут, считаем
    // чётность»). Счёт держит WholeStone.ЛучшаяЧётность, и зовут его отсюда
    // одной строкой в PlanCheckerboard

    // ---------------- работа ----------------

    /// <summary>
    /// Клетка, в которой стоят ноги бота, — ОДНИМ входом
    /// (<see cref="Movement.FeetCell"/>), а не по <c>floor(Y)</c>: на блоке
    /// выше шага, но ниже целого куба (своя каменная тропа, 0.9375) тело стоит
    /// в клетке НАД ним, и своя копия счёта разошлась бы с движением.
    ///
    /// Стояло здесь <c>ctx.World.StandingCellAt(ctx.Self.Position)</c> — то же
    /// правило и та же точка (<c>SelfState.Position</c> и <c>Movement</c> берут
    /// координаты у одного <c>entities.Self</c>), то есть вторая дверь в ту же
    /// комнату. Дверь одна: у <c>StandingCellAt</c> есть просадка (<c>sink</c>),
    /// и добавь её кто-нибудь одному входу — карьер и движение стали бы считать
    /// дно ямы по-разному.
    /// </summary>
    private BlockPos? Feet => ctx.Movement.FeetCell;

    /// <summary>
    /// Сколько своих мест свободно. Вопрос к своему состоянию: ответ там один
    /// на всех, кто копает, — карьер, штольня и склад считали его по-своему
    /// </summary>
    private int FreeSlots() => ctx.Self.FreeCarrySlots();

    private Dictionary<string, int> Snapshot() => ctx.Self.CarrySnapshot();

    /// <summary>Суммарный остаток прочности всех инструментов — по нему считаем расход.</summary>
    private int TotalDurability() => ctx.Wear.All().Sum(w => w.Remaining);

    /// <summary>
    /// Опасная жидкость вплотную к клетке: вскрывать такую нельзя — за камнем
    /// стоит лава или вода, и они хлынут в карьер. Возвращает саму клетку-соседа.
    /// </summary>
    private (BlockPos At, bool Lava)? DangerNear(BlockPos p) =>
        ctx.World.LiquidNear(p, AvoidWater);

    /// <summary>
    /// Можно ли сломать клетку прямо под собой и упасть в неё.
    ///
    /// Смотрим не «есть ли пол», а на СКОЛЬКО падать: игра бьёт за падение
    /// выше трёх с небольшим блоков, и копать себе шахту в пустоту — не то же
    /// самое, что снять один слой. Лава и вода внизу отменяют всё сразу.
    /// </summary>
    private bool SafeToDropInto(BlockPos cell)
    {
        if (ctx.World.IsDangerousLiquid(cell.X, cell.Y, cell.Z) ||
            (AvoidWater && ctx.World.IsWater(cell.X, cell.Y, cell.Z)))
            return false;

        // Ищем, куда приземлимся: первый твёрдый блок ниже клетки
        for (int drop = 1; drop <= MaxSelfDrop + 1; drop++)
        {
            int y = cell.Y - drop;
            if (ctx.World.IsDangerousLiquid(cell.X, y, cell.Z))
                return false;
            if (AvoidWater && ctx.World.IsWater(cell.X, y, cell.Z))
                return false;
            if (!ctx.World.IsPassable(cell.X, y, cell.Z))
                return drop <= MaxSelfDrop;   // приземление в пределах терпимого
        }
        return false;   // под клеткой пропасть — туда не прыгаем
    }

    /// <summary>Не обвалится ли потолок над головой там, где бот стоит сейчас.</summary>
    public bool CeilingSafeAt(BlockPos feet)
    {
        if (!CareAboutCeiling)
            return true;
        // Блок прямо над головой: ноги в feet, голова в feet+1, потолок в feet+2
        var above = new BlockPos(feet.X, feet.Y + 2, feet.Z);
        // Нестабильность есть только у породы с поведением UnstableRock:
        // доски и земля на голову не падают
        if (!IsUnstableRock(above))
            return true;
        return Instability(above) < MaxInstability;
    }

    /// <summary>
    /// Отойти, если бот стоит на клетке, которую собрался копать (или в ней).
    /// Копать под собой нельзя: живой игрок так проваливается.
    ///
    /// Сам приём — общий (<see cref="Movement.StepAsideFromAsync"/>): им же
    /// уходит с клетки дорога, когда её первый ряд начинается под ногами.
    /// Своё тут одно — не вставать под нестабильную породу.
    /// </summary>
    private Task<bool> StepAsideAsync(BlockPos cell, CancellationToken ct) =>
        ctx.Movement.StepAsideFromAsync(cell, radius: 3,
            spot => !CareAboutCeiling || CeilingSafeAt(spot), ct);

    /// <summary>
    /// Уйти из-под нестабильного потолка на ближайшее безопасное место.
    /// Игра роняет породу слоями (collapseLayer), стоять под ней нельзя.
    /// </summary>
    private async Task<bool> MoveOutFromUnderCeilingAsync(CancellationToken ct)
    {
        if (Feet is not { } f)
            return false;

        var spots = new List<(BlockPos Spot, double Cost)>();
        for (int dx = -4; dx <= 4; dx++)
            for (int dz = -4; dz <= 4; dz++)
            {
                if (dx == 0 && dz == 0)
                    continue;
                int x = f.X + dx, z = f.Z + dz;
                if (ctx.World.FindStandableY(x, f.Y, z, up: 1, down: 2) is not { } y)
                    continue;
                var spot = new BlockPos(x, y, z);
                if (!CeilingSafeAt(spot))
                    continue;
                spots.Add((spot, Distance(f.X, f.Y, f.Z, spot.X, spot.Y, spot.Z)));
            }

        // Из-под осыпающейся породы уходят БЫСТРО и в ближайшее безопасное
        // место, а не обходят десяток вариантов
        foreach (var (spot, _) in spots.OrderBy(t => t.Cost).Take(3))
        {
            if (ct.IsCancellationRequested)
                break;
            if (await ctx.Movement.MoveToCellAsync(spot, ct) &&
                Feet is { } now && CeilingSafeAt(now))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Подойти поближе, если до клетки не дотянуться.
    ///
    /// СНАЧАЛА СЧИТАЕМ, ПОТОМ ИДЁМ. Прежняя версия перебирала кольца вокруг
    /// клетки и шла К КАЖДОМУ кандидату целиком: до трёх десятков полноценных
    /// походов на один блок, почти все впустую. Со стороны это выглядело
    /// ровно так, как и пожаловался заказчик: «дал копать карьер, а он бегает
    /// в середине». Теперь место выбирается расчётом — стоячая клетка, с
    /// которой цель попадает в руку, ближайшая к нам, — и поход ровно один.
    /// </summary>
    private async Task<bool> ComeCloserAsync(BlockPos cell, CancellationToken ct)
    {
        if (Feet is null)
            return false;

        // Пробуем не больше трёх мест: если и оттуда не дотянуться, дело не
        // в расстоянии, а в стене — бегать дальше бессмысленно
        foreach (var spot in StandSpotsFor(cell).Take(3))
        {
            if (ct.IsCancellationRequested)
                break;
            if (await ctx.Movement.MoveToCellAsync(spot, ct) && ctx.Hands.InReach(cell))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Подобрать выпавшее, НЕ ОТБИРАЯ ТЕЛО У СЕБЯ ЖЕ, — ОБЩИМ ВХОДОМ
    /// <see cref="Mining.CollectDropsAsync"/>, а не своим.
    ///
    /// Живой случай, ради которого решение вообще появилось: карьер шёл под
    /// владением «команда карьер», а сбор после каждого слоя звался путём,
    /// который тело БЕРЁТ. Владение подбора фоновое (рутина), карьерное —
    /// важнее, и распорядитель честно отказывал: «[тело] «подбор» не берёт
    /// тело: занято «команда карьер»» — по строке на каждый слой. В отчёте это
    /// вышло так: сломано 67 клеток, получено soil-low-none x24, всё остальное
    /// осталось лежать на дне.
    ///
    /// ЗДЕСЬ ЖИЛА ВТОРАЯ КОПИЯ ЭТОГО РЕШЕНИЯ. Карьер писал <c>takeBody: false</c>
    /// литералом, а добыча к тому времени завела на тот же вопрос ЧИСТОЕ ПРАВИЛО
    /// (<see cref="Mining.PickupTakesOwnBody"/>) — и поправь его кто-нибудь,
    /// карьер про поправку не узнал бы. Ровно та же беда, что уже случилась с
    /// метеоритом: два места, одно починили, второе осталось. Держит тело здесь
    /// сама работа карьера, поэтому довод и умалчивается — у входа он такой по
    /// умолчанию.
    /// </summary>
    private Task<int> CollectDropsAsync(double radius, double seconds, CancellationToken ct) =>
        ctx.Mining.CollectDropsAsync(radius, seconds, ct);

    private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// ЧТО ДОЛЖНО СТОЯТЬ В ГОТОВОМ КАРЬЕРЕ — раскладка «задумано» клетка за
    /// клеткой, из ТОГО ЖЕ плана, по которому копали.
    ///
    /// Всё, что назначено к выемке (и «в крошку», и «целым»), должно стать
    /// ПУСТОТОЙ: забытый столб посреди ямы — это не выкопанная яма, а решётка,
    /// на которую и жаловался заказчик («он сделал 2 слоя и затупил»). А вот
    /// ступени пандуса обязаны ОСТАТЬСЯ: их не ломают никогда, и пропавшая
    /// ступень значит, что из ямы больше нет выхода ногами
    /// (см. <see cref="RampCoversEveryLayer"/>).
    ///
    /// Правило чистое: сверять готовую яму живьём — это сперва её выкопать.
    /// </summary>
    public static IReadOnlyList<MeantCell> MeantCells(QuarryPlan plan)
    {
        var all = new List<MeantCell>();
        // Пандус первым: твердь всё равно побеждает пустоту, но так порядок
        // обхода начинается с того, чего в яме быть ОБЯЗАНО
        foreach (var step in plan.Ramp)
            all.Add(new MeantCell(step, Meant.Solid));
        foreach (var cell in plan.Clear)
            all.Add(new MeantCell(cell, Meant.Clear));
        foreach (var cell in plan.Harvest)
            all.Add(new MeantCell(cell, Meant.Clear));
        return BuildCheck.Merge(all);
    }

    // ---------------- выход из своей же ямы ----------------

    /// <summary>
    /// Есть ли ступень пандуса на КАЖДОМ слое: только тогда по нему выходят
    /// ногами. Ступени в плане идут по одной на слой и соседями по горизонтали
    /// (см. <see cref="BuildRamp"/>), а прыжок в игре берёт ровно один блок:
    /// пропущенный слой — это стена в два блока, и пандус на ней кончается.
    /// </summary>
    public static bool RampCoversEveryLayer(QuarryPlan plan)
    {
        var levels = new HashSet<int>();
        foreach (var step in plan.Ramp)
            levels.Add(step.Y);
        for (int y = plan.Min.Y; y <= plan.Max.Y; y++)
            if (!levels.Contains(y))
                return false;
        return true;
    }

    /// <summary>
    /// Чем будем выбираться с глубины этого карьера. Спрашивается ДО работы:
    /// на дне выбирать уже не из чего.
    ///
    /// Способы по возрастанию цены:
    /// 1. пандус — ступени, которые мы и так бережём: выход ногами, даром;
    /// 2. лестница по стене — стена у карьера есть всегда, лестницы из сумок;
    /// 3. столб под собой — блоки из сумок, по блоку на клетку подъёма.
    /// Сами лестницы и блоки считает штольня (<see cref="Tunnel.CanGetBack"/>):
    /// вопрос «хватит ли сил подняться на N блоков» один и тот же для колодца
    /// и для карьера, а второй счёт рано или поздно разошёлся бы с первым.
    /// </summary>
    public (bool Can, string Why) CanEscape(QuarryPlan plan)
    {
        // Со дна до края подниматься ровно на столько блоков, сколько слоёв
        // мы снимем: пол опустится на дно, а край останется на месте
        int depth = plan.Max.Y - plan.Min.Y + 1;
        if (depth <= 1)
            return (true, "яма в один слой — выйду прыжком");

        if (RampCoversEveryLayer(plan))
            return (true, $"по пандусу: ступень на каждом из {depth} слоёв");

        // Почему пандуса нет — говорим прямо: чаще всего это поправимо одной
        // настройкой, и человеку важно знать какой
        string noRamp = plan.Ramp.Count == 0
            ? (KeepExitRamp
                ? "пандус не встал: кольцо области короче трёх клеток"
                : "пандус выключен (KeepExitRamp)")
            : "пандус обрывается: ступень есть не на каждом слое";

        if (!MayClimbOut)
            return (false, $"{noRamp}, а строить выход не велено (MayClimbOut)");

        // Штольни у бота может не быть (собран урезанно) — тогда честно
        // говорим, что не считали, а не выдаём догадку за проверку
        if (ctx.Tunnel is not { } tunnel)
            return (true, $"{noRamp}; чем подниматься на {depth} бл — посчитать нечем");

        var force = tunnel.CanGetBack(depth);
        return force.Can ? force : (false, $"{noRamp}; {force.Why}");
    }

    /// <summary>
    /// Подняться до нужной высоты лестницей по стене или столбом под собой.
    /// Возвращает true, если что-то построили и поднялись.
    ///
    /// ЗДЕСЬ ТОЛЬКО РАЗРЕШЕНИЕ И СЛОВА. Сам подъём — <see cref="Scaffolding.ClimbOutToAsync"/>,
    /// туда же ушли порог «меньше двух блоков — запрыгну сам» и правило успеха
    /// «ноги правда поднялись». Ровно такая же копия стояла у рудокопа
    /// (<c>Ores.ClimbOutToAsync</c>); их называли двойной механикой три волны
    /// подряд, и теперь механика одна на обоих.
    /// </summary>
    private async Task<bool> ClimbOutToAsync(int wantedY, CancellationToken ct)
    {
        if (!MayClimbOut || ctx.Scaffolding is not { } scaffolding)
            return false;

        var climb = await scaffolding.ClimbOutToAsync(wantedY, () => Feet, ct,
            need => OnLog?.Invoke($"со дна карьера до края {need} бл — строю выход наверх"));
        // Карьер, в отличие от рудокопа, пересказывает и сам отчёт подъёма:
        // человек по нему разбирает, ЧЕМ бот выбирался и на чём встал
        if (climb.Why.Length > 0)
            OnLog?.Invoke(climb.Why);
        return climb.Up;
    }

    /// <summary>
    /// Сколько блоков можно принести в жертву ради доступа к одной клетке.
    /// Ноль — не жертвовать вовсе (тогда недоступное просто пропускается).
    ///
    /// Счёт идёт НА КЛЕТКУ И НА ВСЮ РАБОТУ, а не на один подступ: к одной клетке
    /// карьер подходит до четырёх раз, и пока счётчик жил внутри подступа, предел
    /// 2 давал до шести сломанных соседей. Держит счёт <see cref="SacrificeLedger"/>.
    /// </summary>
    public int MaxSacrificePerCell { get; set; } = 2;

    /// <summary>
    /// Прокопаться к недоступной клетке.
    ///
    /// Задача возникает из самой шахматки: между ботом и целевым блоком может
    /// стоять сосед, который по плану ломается ПОЗЖЕ, и бот залипал —
    /// «не дотянуться», пропуск, дальше по списку.
    ///
    /// Формула жертвы, по возрастанию цены:
    /// 1. сосед, который и так помечен в крошку — цена НОЛЬ, его всё равно
    ///    ломать, просто раньше срока;
    /// 2. сосед вне области карьера — цена мала: он в план не входил;
    /// 3. соседа из «целых» не трогаем никогда — это ровно тот блок, ради
    ///    которого шахматка и затевалась.
    /// Больше <see cref="MaxSacrificePerCell"/> жертв за клетку не приносим:
    /// если и это не открыло доступ, дело не в соседях. Счёт ведёт
    /// <see cref="SacrificeLedger"/> — он живёт на всю работу, а не на один
    /// подступ: к одной и той же клетке сюда заходят до четырёх раз.
    ///
    /// ЖЕРТВУЕМ ТЕМ, ДО ЧЕГО САМИ ДОТЯГИВАЕМСЯ. Иначе выходит круг: чтобы
    /// подойти к клетке, ломаем соседа, к которому тоже не подойти. Поэтому
    /// первыми идут соседи, до которых рука достаёт прямо сейчас.
    /// </summary>
    private async Task<bool> OpenAccessAsync(Run state, BlockPos cell, CancellationToken ct)
    {
        if (MaxSacrificePerCell <= 0 || Feet is not { } from)
            return false;
        // Право платить за ЭТУ клетку могло кончиться на прошлом подступе —
        // тогда ломать больше нечего, и звать дорогу заново незачем
        if (!state.Sacrifice.MayPayFor(cell, MaxSacrificePerCell))
            return false;

        (int dx, int dy, int dz)[] around =
        [
            (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, -1, 0)
        ];

        int broke = 0;
        foreach (var (dx, dy, dz) in around
                     .Select(d => (d, near: new BlockPos(cell.X + d.dx, cell.Y + d.dy,
                         cell.Z + d.dz)))
                     // Сперва то, до чего уже дотягиваемся, потом — что ближе
                     .OrderBy(t => ctx.Hands.InReach(t.near) ? 0 : 1)
                     .ThenBy(t => Distance(from.X, from.Y, from.Z, t.near.X, t.near.Y, t.near.Z))
                     .Select(t => t.d))
        {
            if (!state.Sacrifice.MayPayFor(cell, MaxSacrificePerCell) ||
                ct.IsCancellationRequested)
                break;

            var near = new BlockPos(cell.X + dx, cell.Y + dy, cell.Z + dz);
            if (!ctx.World.IsSolid(near.X, near.Y, near.Z))
                continue;
            if (state.IsHarvestCell(near))
                continue;   // это целый блок — ради него всё и затеяно
            if (state.IsRampCell(near))
                continue;   // это ступень пандуса: сломать её значит закопать себя
            if (!ctx.Hands.InReach(near) &&
                (StandSpotFor(near) is null || !await ComeCloserAsync(near, ct)))
                continue;   // до самой жертвы не добраться — это не выход, а круг

            var sacrifice = await ctx.Mining.BreakAsync(near, ct);
            if (!sacrifice.Success)
                continue;

            broke++;
            // ЧЕМ ИМЕННО ЗАПЛАТИЛИ, РАЗБИРАЕТ СЧЁТ (SacrificeLedger), и он же
            // помнит, сколько уже отдано за эту клетку. Своя клетка «в крошку»,
            // сломанная раньше срока, платой не зовётся — её и так ломать, это
            // обычная работа, и записать её надо в сделанное, иначе после
            // перерыва бот пойдёт ломать то, чего в мире уже нет
            if (!state.Sacrifice.Note(cell, ownPlanCell: state.IsPlanCell(near)))
            {
                state.Broken++;
                state.Done.Add(near);
            }
            await CollectDropsAsync(4, 4, ct);
            if (ctx.Hands.InReach(cell) || await ComeCloserAsync(cell, ct))
                return true;
        }
        return broke > 0 && ctx.Hands.InReach(cell);
    }

    /// <summary>
    /// Копать карьер по центру верхнего слоя — способом, который выбрала роль
    /// (<see cref="Cut"/>).
    /// </summary>
    public Task<QuarryReport> QuarryAsync(BlockPos topCentre, int size = 7, int depth = 3,
        CancellationToken ct = default) =>
        QuarryAsync(Plan(topCentre, size, depth), ct);

    /// <summary>
    /// Отработать готовый план. Порядок принципиален: слой чистится сверху вниз,
    /// и только ПОСЛЕ того как убран слой снизу, у клеток слоя выше пропадает
    /// последний твёрдый сосед — тогда их и берут целыми.
    ///
    /// <paramref name="deadline"/> — общий срок на всю работу, если она идёт в
    /// несколько заходов. Без него срок <see cref="MaxSeconds"/> начинался бы
    /// заново после каждого перерыва, и «карьер на четверть часа», прерванный
    /// трижды, тянулся бы час: человек ставил бы одну мерку, а получал другую.
    ///
    /// ОТКРЫТ ДЛЯ ПОДМЕНЫ НА СТЕНДЕ — ровно затем же, зачем открыты
    /// <see cref="MiningOrder.FetchAsync"/> и <see cref="Roads.BuildAsync"/>:
    /// без этого ШОВ «прервали — вернись к ОСТАТКУ» проверить нечем. Приёмка
    /// подставила сюда «начать план заново» — и все 3532 теста остались
    /// зелёными, хотя бот заново перекапывал бы выкопанное. Своей работы в
    /// подмене нет: заход по-прежнему один и тот же.
    /// </summary>
    public virtual async Task<QuarryReport> QuarryAsync(QuarryPlan plan,
        CancellationToken ct = default, DateTime? deadline = null)
    {
        // ШОВ: ПОКА МЫ КОПАЕМ, ДВИЖЕНИЕ ЗНАЕТ, ЧТО ОНО ВНУТРИ НАШЕЙ РАБОТЫ.
        //
        // ЖИВОЙ СЛУЧАЙ, ради которого это появилось. Копая, бот раз за разом не
        // мог подойти к очередной клетке; движение честно отвечало «обычной
        // дорогой отсюда не выйти» — и принималось искать выход: рыло лестницы
        // наружу, а собственный недокопанный карьер принимало за пропасть и
        // мостило. Заказчик: «дал задание рыть карьер, он начал из него делать
        // дополнительные выходы, и зачем-то вылез и начал рыть с земли тоннель
        // под землю в другую сторону».
        //
        // Границы сообщаем МЫ, решает движение (Movement.LeaveToOwnDig): ссылки
        // вверх, на карьер, у движения нет и быть не должно. Выход из этой ямы —
        // наша забота: пандус (ступень на каждом слое) и, если его сорвало,
        // ClimbOutToAsync в самом конце
        ctx.Movement.OwnDig = new Movement.DigSite(plan.Min, plan.Max, "карьер");
        try
        {
            var report = await DigAsync(plan, ct, deadline);
            // ЯМА ВЫКОПАНА — СВЕРЯЕМСЯ И УБИРАЕМ ЗА СОБОЙ. Только при
            // законченном плане: прерванный заход будет продолжен, и снимать
            // сейчас столб, по которому бот через минуту полезет обратно,
            // значит мешать самому себе (см. Roads.BuildAsync — правило то же)
            if (report.Stop != QuarryStop.Done)
            {
                // НО МОЛЧА УХОДИТЬ НЕЛЬЗЯ. Прерванный заход леса не снимает —
                // и раньше он про них вовсе не говорил, хотя дорога в том же
                // случае говорила. Один механизм, один голос: слова берутся у
                // самих лесов (TempBlocks.OweRule)
                return ctx.Temp.Owe("карьер") is { Length: > 0 } owed
                    ? report with { Reason = report.Reason + "; " + owed }
                    : report;
            }
            string after = await AfterWorkAsync(plan, ct);
            return after.Length > 0 ? report with { Reason = report.Reason + "; " + after } : report;
        }
        finally
        {
            // СНИМАЕМ ВСЕГДА, чем бы ни кончилось. Забытая метка означала бы,
            // что бот больше никогда не выберется из НАСТОЯЩЕЙ ямы: рефлекс
            // выживания уводит тело хоть на первой клетке, и дорога до еды
            // пошла бы уже с завязанными глазами
            ctx.Movement.OwnDig = null;
        }
    }

    /// <summary>
    /// ЧТО ДЕЛАЕТСЯ, КОГДА ЯМА ВЫКОПАНА: сверить её с планом и снять свои
    /// временные леса.
    ///
    /// ЗАКАЗ ЧЕЛОВЕКА, дословно: «надо проверять объекты потом на лишние блоки,
    /// разрешить строить временные леса во время стройки или копания объектов,
    /// которые в конце убираем».
    ///
    /// Своих механизмов тут ни одного: раскладку даёт <see cref="MeantCells"/>
    /// из того же плана, по которому копали, сверяет <see cref="BuildCheck"/>,
    /// леса снимает <see cref="TempBlocks.TakeDownAsync"/>. Здесь только «когда».
    ///
    /// СНАЧАЛА ЛЕСА, ПОТОМ СВЕРКА, и порядок этот не случаен: собственный столб
    /// посреди ямы — это и есть «лишний блок», и сверка честно записала бы его
    /// в лишние, а потом ломала бы второй раз то, что уборка уже сняла.
    ///
    /// Возвращает жалобу для отчёта или пустую строку, если всё чисто.
    /// </summary>
    private async Task<string> AfterWorkAsync(QuarryPlan plan, CancellationToken ct)
    {
        var complaints = new List<string>();

        if (ctx.Temp.TakeDownAfterWork && ctx.Temp.Count > 0)
        {
            var cleanup = await ctx.Temp.TakeDownAsync(ct: ct);
            if (!cleanup.Success)
                complaints.Add(cleanup.ToString());
        }

        if (ctx.Check.AfterWork)
        {
            var check = await ctx.Check.CheckAsync("карьер", MeantCells(plan), ct);
            if (!check.Success)
                complaints.Add(check.ToString());
        }

        return string.Join("; ", complaints);
    }

    /// <summary>Сама работа. Отдельно от <see cref="QuarryAsync(QuarryPlan, CancellationToken, DateTime?)"/>
    /// затем, чтобы метка «я внутри своей выработки» снималась одним finally, а не
    /// в каждом из десятка выходов.</summary>
    private async Task<QuarryReport> DigAsync(QuarryPlan plan, CancellationToken ct,
        DateTime? deadline)
    {
        var started = Now();
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);
        var state = new Run(plan, skips);

        if (!ctx.World.GameBlocksReady)
            return Finish(state, QuarryStop.NotReady, "реестр блоков сервера ещё не пришёл",
                plan.Min, new Dictionary<string, int>(), 0, started);

        var before = Snapshot();
        int durabilityBefore = TotalDurability();
        var home = Feet ?? plan.Max;
        state.Deadline = deadline ?? started.AddSeconds(MaxSeconds);

        OnLog?.Invoke(plan.ToString());
        // «Целых блоков не будет» — новость разная в зависимости от того, ПОЧЕМУ
        // их нет. При съёме целиком это выбор человека, и объяснять ему тут
        // нечего; при шахматке — беда с областью, и о ней надо сказать
        if (plan.Harvest.Count == 0 && plan.Cut == QuarryCut.Checkerboard)
            OnLog?.Invoke("целых блоков не выйдет: область меньше 3 клеток по какой-то стороне " +
                          "или её края упираются в камень — изолировать нечего");

        // СИЛЫ НА ПОДЪЁМ СЧИТАЕМ ДО ПЕРВОГО УДАРА. Старый карьер докладывал
        // «из ямы не выбраться» уже со дна — то есть после того, как сам себя
        // и закопал. Отказ, который ещё что-то меняет, бывает только здесь
        var escape = CanEscape(plan);
        OnLog?.Invoke((escape.Can ? "выход наверх — " : "выхода наверх нет: ") + escape.Why);
        if (CheckEscapeBeforeStart && !escape.Can)
            // ДВЕ РАЗНЫЕ ПРИЧИНЫ ПОД ОДНИМ «НЕ ВЫБРАТЬСЯ», и путать их нельзя:
            // «строить выход не велено» — это запрет роли, его лечит человек
            // ручкой; «нечем подниматься» — это кончившийся запас. Различает
            // их та же настройка, по которой отказ и составлен
            return Finish(state, QuarryStop.NoEscape, $"копать не начинаю — {escape.Why}",
                home, new Dictionary<string, int>(), 0, started,
                MayClimbOut ? Setback.NoMaterial : Setback.Stopped);

        // СЛОИ СВЕРХУ ВНИЗ, И РЕШЁТКА ДОБИВАЕТСЯ ДО СПУСКА НИЖЕ.
        //
        // Чистим слой y, затем СРАЗУ берём целыми слой y+1: его нижние соседи
        // только что исчезли, значит столбы повисли и падают целыми. Спуститься
        // ниже, не добив их, нельзя — оставленные столбы копятся слой за слоем
        // и превращаются в лес, на который бот и залезает. Ровно это видно в
        // живом журнале: он стоял на столбах и не доставал до пола двумя
        // блоками ниже. Что до нижних соседей — их даёт слой-жертва, каждый
        // второй сверху (см. IsHarvestLayer): без него к ним не подойти вовсе.
        for (int y = plan.Max.Y; y >= plan.Min.Y && state.Stop == QuarryStop.Done; y--)
        {
            await MineLayerAsync(state, plan.Clear, y, whole: false, ct);
            if (state.Stop == QuarryStop.Done && CollectRubble)
                await CollectDropsAsync(CollectRadius, CollectSeconds, ct);

            if (state.Stop == QuarryStop.Done && y + 1 <= plan.Max.Y)
            {
                await MineLayerAsync(state, plan.Harvest, y + 1, whole: true, ct);
                if (state.Stop == QuarryStop.Done && CollectWhole)
                    await CollectDropsAsync(CollectRadius, CollectSeconds, ct);
            }
        }

        // Нижний слой: его целые клетки изолируются, только если снизу уже пусто
        // (старая пещера) — иначе они честно уходят в пропуск
        if (state.Stop == QuarryStop.Done)
        {
            await MineLayerAsync(state, plan.Harvest, plan.Min.Y, whole: true, ct);
            if (CollectWhole)
                await CollectDropsAsync(CollectRadius, CollectSeconds, ct);
        }

        // Последний проход за добычей — ВСЕГДА, а не только при успехе.
        // Сбор по слоям стоит внутри «if (state.Stop == Done)», и любая
        // досрочная остановка (время, полные сумки, сломался инструмент)
        // оставляла всю добычу лежать: отсюда и «сломано 9, получено: ничего»
        if (CollectRubble || CollectWhole)
            await ctx.Pickup.CollectAsync(Math.Max(CollectRadius, 8),
                maxSeconds: CollectSeconds * 2, ct: ct, owner: "добыча карьера",
                takeBody: false);   // тело уже наше — отбирать его у себя нельзя

        // ВЫБИРАЕМСЯ. Сперва ногами — ради этого и берегли пандус: он даром и
        // не оставляет в мире лишнего. Не вышло (пандуса нет, ступень сорвало
        // обвалом, путь завалило) — строим выход, как строил бы игрок:
        // лестницу по стене карьера, а нет лестниц — столб под собой.
        // Раньше здесь был только поиск пути, и бот честно докладывал «не
        // выбраться», оставаясь на дне навсегда
        var stop = state.Stop;
        string reason = state.Reason;
        if (!ct.IsCancellationRequested && Feet is { } low && low.Y < home.Y)
        {
            bool escaped = await ctx.Movement.TravelToAsync(home, EscapeSeconds, ct);
            if (!escaped && await ClimbOutToAsync(home.Y, ct))
                // Поднялись — а домой дойдём и поверху; если и туда пути нет,
                // главное уже сделано: из ямы мы вылезли
                escaped = await ctx.Movement.TravelToAsync(home, EscapeSeconds, ct) ||
                          (Feet is { } up && up.Y >= home.Y);
            if (!escaped)
            {
                // Причину прежней остановки не затираем: человеку нужно знать
                // и почему бот бросил копать, и что он остался на дне
                stop = QuarryStop.NoEscape;
                reason = (state.Stop != QuarryStop.Done ? state.Reason + "; " : "") +
                         $"из ямы не выбраться: обратно в {home} пути нет" +
                         (MayClimbOut ? ", и построить выход не вышло" : " (строить выход не велено)");
                // Те же две причины под одним словом, что и у проверки перед
                // началом: запрет роли лечится ручкой, а «не вышло» — запасом
                state.Live = MayClimbOut ? Setback.NoRoute : Setback.Stopped;
            }
        }

        int spent = Math.Max(0, durabilityBefore - TotalDurability());
        var gained = Diff(before, Snapshot());
        return Finish(state, stop, reason, state.Last ?? plan.Max, gained, spent, started);
    }

    // ---------------- прервали — вернись и доделай ----------------

    /// <summary>
    /// ОСТАТОК ПЛАНА: тот же карьер, но без клеток, которых в мире уже нет.
    ///
    /// ЧИСТОЕ ПРАВИЛО, и оно отвечает на «с какого места возвращаться». Пандус,
    /// границы и чётность берутся прежние: это тот же карьер, а не новый.
    /// Пропущенные клетки в остатке ОСТАЮТСЯ нарочно — «не изолирован» значит
    /// «рано», и на следующем заходе, когда слой снизу убран, такая клетка как
    /// раз и даст целый блок.
    /// </summary>
    public static QuarryPlan Remaining(QuarryPlan plan, IReadOnlyCollection<BlockPos> done)
    {
        var gone = done as HashSet<BlockPos> ?? [.. done];
        return new QuarryPlan
        {
            Min = plan.Min,
            Max = plan.Max,
            Parity = plan.Parity,
            Clear = [.. plan.Clear.Where(c => !gone.Contains(c))],
            Harvest = [.. plan.Harvest.Where(c => !gone.Contains(c))],
            Ramp = plan.Ramp,
            Cut = plan.Cut
        };
    }

    /// <summary>
    /// Сложить два отчёта в один: работа шла в несколько заходов, а человеку
    /// нужен ОДИН итог.
    ///
    /// ЧИСТОЕ ПРАВИЛО. Живой урок из журнала: карьер доложил «сломано 67 из
    /// 720» и умолк навсегда. Если после возврата показать только последний
    /// заход, выйдет та же ложь наизнанку — «сломано 653 из 653», как будто
    /// первых шестидесяти семи не было. Поэтому «сколько было в плане» берётся
    /// у ПЕРВОГО отчёта, «чем кончилось» — у последнего, а всё считаемое
    /// складывается.
    /// </summary>
    public static QuarryReport Merge(QuarryReport first, QuarryReport then)
    {
        var gained = new Dictionary<string, int>(first.Gained, StringComparer.Ordinal);
        foreach (var (code, count) in then.Gained)
            gained[code] = gained.GetValueOrDefault(code) + count;

        var done = new HashSet<BlockPos>(first.Done);
        foreach (var cell in then.Done)
            done.Add(cell);

        return new QuarryReport(then.Stop, then.Reason, then.StoppedAt,
            first.Planned,
            first.Broken + then.Broken,
            first.Isolated + then.Isolated,
            first.AutoDropped + then.AutoDropped,
            // ПРОПУСКИ НЕ СКЛАДЫВАЮТСЯ, а берутся у последнего захода. Остаток
            // плана сохраняет пропущенные клетки (см. Remaining), и последний
            // заход обходит их все заново — значит его счёт и есть правда о
            // том, что осталось нетронутым. Сложение же удвоило бы каждую
            // клетку, которую на первом заходе пропустили, а на втором взяли
            then.Skipped,
            first.WholeGained + then.WholeGained,
            first.RubbleGained + then.RubbleGained,
            first.DurabilitySpent + then.DurabilitySpent,
            first.Seconds + then.Seconds,
            gained, then.Skips)
        {
            Done = done,
            Returns = first.Returns + then.Returns + 1,
            Sacrificed = first.Sacrificed + then.Sacrificed,
            // ПОМЕХА — У ПОСЛЕДНЕГО, И ТОЛЬКО У НЕГО. Она про то, что мешает
            // ПРЯМО СЕЙЧАС: игрок, сошедший с клетки между заходами, помехой
            // быть перестал, и тащить его в итог значило бы объявить карьер
            // остановленным тем, чего уже нет (то же правило у RoadResume.Merge)
            Hindrance = then.Hindrance
        };
    }

    /// <summary>
    /// Копать так, чтобы рефлекс выживания не убивал работу насовсем.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 21:37-21:39): «!карьер 7 15», через полторы минуты
    /// рядом падает еда, рефлекс забирает тело — и карьер, сломав 67 клеток из
    /// 720, умирает навсегда. Человек увидел это так: «скопал только часть
    /// сверху и не стал углубляться по заданию».
    ///
    /// Здесь только СВОЯ половина дела: чем продолжать (остаток плана,
    /// <see cref="Remaining"/>) и как сложить отчёты (<see cref="Merge"/>).
    /// Когда возвращаться, сколько раз и что сказать, бросая, знает общий
    /// механизм <see cref="Resume"/> — он же и держит тело.
    ///
    /// =====================================================================
    /// СРОКА НА ЗАХОД У КАРЬЕРА НЕТ, И ЭТО ВЫБОР, А НЕ НЕДОДЕЛКА.
    /// =====================================================================
    ///
    /// ЧТО ИЗ ЭТОГО СЛЕДУЕТ, СКАЗАНО ЗДЕСЬ ВСЛУХ, ЧТОБЫ ЧИТАТЕЛЬ НЕ ЖДАЛ
    /// НЕСБЫТОЧНОГО: <see cref="QuarryStop.Timeout"/> у карьера значит ровно
    /// «время ВСЕЙ работы кончилось», и второго захода ПОСЛЕ СРОКА не бывает
    /// НИКОГДА — ни по какому пережитому слову, включая
    /// <see cref="Setback.Crowd"/>. Живой замер приёмки: <c>заходов=1,
    /// Stop=Timeout, Hindrance=OutOfTime</c>. Так и задумано.
    ///
    /// ПОЧЕМУ СРОК ОДИН. Человек сказал «карьер на четверть часа» про СВОИ
    /// часы. Дай мы заходу свой срок сверх общего — «четверть часа»,
    /// прерванная трижды, стала бы часом: человек ставил бы одну мерку, а
    /// получал другую (тот же разбор у <see cref="QuarryAsync(QuarryPlan,
    /// CancellationToken, DateTime?)"/>).
    ///
    /// А ЕСЛИ РЕЗАТЬ СРОК ЗАХОДА ВНУТРИ ОБЩЕГО — по образцу ноги похода
    /// (<see cref="TrekRules.LegSecondsWithin"/>), — то мерка человека цела,
    /// но выгоды нет ни секунды: заход и так копает подряд до самого срока,
    /// а нарезка ДОБАВИТ простоя ровно <c>Resume.WorldPauseSeconds</c> (5 с)
    /// на каждый шов — то есть отнимет у ямы время, ничего не дав взамен.
    /// Хуже того: заход, кончившийся по своему потолку и НИЧЕГО не переживший,
    /// доложил бы <see cref="Setback.OutOfTime"/> — постоянное слово, — и
    /// здоровый карьер бросил бы яму на первом же шве, не истратив остатка.
    /// Чтобы этого не случилось, пришлось бы завести карьеру ещё и отдельное
    /// слово «заход кончился, а работа — нет» (<see cref="Setback.Unfinished"/>),
    /// то есть ВТОРОЕ устройство поверх первого.
    ///
    /// ЧЕГО МЫ ЭТИМ ЛИШАЕМСЯ — ЧЕСТНО. Клетки, обойдённые по ПРОХОДЯЩЕЙ
    /// причине («стою на ней, отойти некуда» — <see cref="Setback.Crowd"/>,
    /// «мир ещё не доехал»), внутри одного захода больше не пробуются:
    /// пропуск помечен <c>Handled</c> и в конец слоя не откладывается. Вернуть
    /// их может только НОВЫЙ заход через <see cref="Remaining"/> — а он после
    /// срока и не наступает. Заходов у карьера от этого не ноль: их дают
    /// отобранное тело (<c>Ending.Preempted</c>) и недоехавший реестр
    /// (<see cref="QuarryStop.NotReady"/>). Обе беды, в отличие от срока,
    /// оставляют работе время: реестр не тратит его вовсе, а рефлекс отдаёт
    /// тело обратно, пока часы работы ещё идут. А если и у них часы вышли —
    /// второго захода не будет и им: откажет застава перед заходом, теми же
    /// словами про отведённые секунды.
    ///
    /// И ЕСЛИ ЭТУ ПОТЕРЮ КОГДА-НИБУДЬ ЗАХОТЯТ ЗАКРЫТЬ — ЧАСЫ ТУТ НИ ПРИ ЧЁМ.
    /// Просят-то не «дай ещё времени», а «зайди в план заново»; мерить это
    /// надо РАБОТОЙ, и такое устройство в проекте уже есть — потолок клеток на
    /// один вызов у дороги (<c>Roads.JudgeRun</c> → <see cref="Setback.Unfinished"/>)
    /// и предел длины у проходки (<c>Tunnel.MaxSteps</c>). Третьего заводить
    /// не надо будет и тогда.
    /// </summary>
    public async Task<QuarryReport> KeepQuarryingAsync(QuarryPlan plan, Resume resume,
        BodyArbiter.Importance level = BodyArbiter.Importance.Command,
        CancellationToken ct = default)
    {
        // Срок общий на всю работу, а не на заход: см. QuarryAsync(deadline)
        // и разбор выбора в описании этого метода
        var deadline = Now().AddSeconds(MaxSeconds);
        var left = plan;
        QuarryReport? total = null;
        // ЧЕМ РАБОТА КОНЧИЛАСЬ — ОДНИМ СЛОВОМ И ОДНО НА ВСЕХ: и возврату для
        // решения, и человеку в отчёт. Держим его здесь нарочно, потому что
        // считается оно ниже ОДИН раз: разойдись эти двое, и отчёт обещал бы
        // «место занял живой, он уйдёт» там, где возврат уже решил «время
        // вышло совсем» — то самое враньё, которого приёмка и ловит
        var помеха = Setback.None;

        var run = await resume.RunAsync($"карьер {plan.Min}..{plan.Max}", level,
            async (attempt, token) =>
            {
                if (attempt > 1)
                    OnLog?.Invoke($"возвращаюсь в карьер: осталось {left.Total} кл. " +
                                  $"из {plan.Total} (заход {attempt})");

                // СРОК ОБЩИЙ НА ВСЮ РАБОТУ, И СПРАШИВАЕМ ЕГО ДО ЗАХОДА, а не
                // после. Без этого возврат, взявшийся за карьер по временной
                // помехе, упирался бы в тот же истёкший срок в первую же
                // клетку — и так до потолка повторов, по пустому заходу на
                // каждый. Ровно так же и по той же причине спрашивают срок
                // наряд и дорога (OrderResume, RoadResume)
                if ((deadline - Now()).TotalSeconds <= 0)
                {
                    OnLog?.Invoke($"на карьер отведённые {MaxSeconds:0} с вышли — " +
                                  "нового захода не начинаю");
                    помеха = Setback.OutOfTime;
                    return (false, помеха);
                }

                var one = await QuarryAsync(left, token, deadline);
                total = total is null ? one : Merge(total, one);
                left = Remaining(left, one.Done);
                // ОТВЕЧАЕМ ЗА СЕБЯ САМИ. Возврат знает только, отбирали ли
                // тело; «яма выкопана» и «сломалась кирка на третьей клетке»
                // для него выглядят одинаково — работа вернулась. Мерку
                // успеха даёт карьер, и она одна: план дошёл до конца.
                //
                // И ПОМЕХУ НАЗЫВАЕМ ВСЛУХ — это и есть просьба заказчика «если
                // мешает игрок, то пробовать заново, а не сбрасывать». Своего
                // решения тут нет: возврат спросит одно чистое правило
                // (Resume.Passing), карьер только переводит своё слово в общее.
                //
                // СРОК ВСЕЙ РАБОТЫ ГАСИТ ПОМЕХУ ЗАХОДА (Resume.WithinBudget):
                // «игрок на клетке» стоит повтора, только пока на этот повтор
                // есть время.
                //
                // И ВОТ ЧТО ЭТО ЗНАЧИТ ИМЕННО ЗДЕСЬ, СКАЗАННОЕ ВСЛУХ, ЧТОБЫ
                // ЧИТАТЕЛЬ НЕ ЖДАЛ ВТОРОГО ЗАХОДА ТАМ, ГДЕ ЕГО НЕ БУДЕТ. Срок
                // у карьера ОДИН на всю работу, другого потолка у захода нет
                // вовсе — значит всякий QuarryStop.Timeout приходит сюда с
                // нулевым остатком, и пережитое временное слово гаснет тут
                // ВСЕГДА. Замерено в лоб: WithinBudget(Crowd, −0.5) = OutOfTime.
                //
                // ТО ЕСТЬ ПАРА «Resume.LivedThrough сохранил проходящее слово →
                // WithinBudget его погасил» на пути СРОКА пуста по устройству,
                // и держится LivedThrough у карьера (Run.Skip) не ею, а второй
                // своей половиной: он ВЫБРАСЫВАЕТ пережитое ПОСТОЯННОЕ слово,
                // и без этого отказ по сроку говорил бы «дальше опасно для
                // жизни, тут нужно другое место» про яму, где 719 клеток из 720
                // стоят себе спокойно (живой случай приёмки 09.09).
                //
                // Второй заход у карьера живёт на других дорогах, и они не
                // тратят срока: отобранное тело и недоехавший реестр
                // (QuarryStop.NotReady). Разбор выбора — в описании метода
                var довёл = one.Stop == QuarryStop.Done;
                // ДОДЕЛАННОЙ РАБОТЕ ПОМЕХИ НЕ ПРИПИСЫВАЕМ — то же правило и по
                // той же причине, что у самого возврата (Resume.RunAsync):
                // иначе карьер, доложивший «яма готова» ровно в последнюю
                // отведённую секунду, показал бы человеку «кончился срок»
                помеха = довёл
                    ? Setback.None
                    : Resume.WithinBudget(one.Hindrance, (deadline - Now()).TotalSeconds);
                return (довёл, помеха);
            }, ct);

        // РАБОТА КОНЧИЛАСЬ СОВСЕМ — И ЛЕСА ПОСЛЕ НЕЁ ОСТАВАТЬСЯ НЕ ДОЛЖНЫ.
        // Заход снимает свои леса только тогда, когда довёл план до конца
        // (иначе он снял бы столб, по которому сам же полезет обратно), а
        // здесь заходов больше не будет — и всё, что осталось, останется
        // навсегда. Убираем даже после неудачного карьера
        if (ctx.Temp.TakeDownAfterWork && ctx.Temp.Count > 0)
        {
            var cleanup = await ctx.Temp.TakeDownAsync(ct: ct);
            if (!cleanup.Success)
                OnLog?.Invoke(cleanup.ToString());
        }

        if (total is null)
            // До первого удара не дошло: тела не дали или отменили сразу.
            // Молчать тут нельзя — человек ждёт ответа на свою команду
            return new QuarryReport(QuarryStop.Cancelled, run.Say, plan.Max, plan.Total,
                0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(),
                new Dictionary<string, int>());

        // ПРИЧИНУ ОСТАНОВКИ НЕ ЗАТИРАЕМ, а дописываем: человеку нужно знать и
        // почему карьер встал, и что к нему больше не вернутся.
        //
        // А ВОТ ПОМЕХУ БЕРЁМ ТУ ЖЕ, ЧТО ОТДАЛИ ВОЗВРАТУ, и берём её ОДНУ на
        // обоих. Сложение отчётов (Merge) знает только слово ПОСЛЕДНЕГО захода
        // и про общий срок не знает ничего — то есть карьер, переживший
        // «место занял живой» и упёршийся в часы, показывал бы наверх
        // временное слово «он уйдёт» после того, как сам уже решил «времени
        // нет совсем». Кто прочтёт это слово завтра (очередь задач, роль),
        // взялся бы за карьер заново — и упёрся бы в тот же истёкший срок
        return total with
        {
            Reason = run.Finished || run.End == BodyArbiter.Ending.None
                ? total.Reason
                : $"{total.Reason}; {run.Say}",
            Hindrance = помеха
        };
    }

    /// <summary>
    /// Карьер по центру верхнего слоя, с возвратом после перерыва — способом,
    /// который выбрала роль (<see cref="Cut"/>).
    /// </summary>
    public Task<QuarryReport> KeepQuarryingAsync(BlockPos topCentre, int size, int depth,
        Resume resume, BodyArbiter.Importance level = BodyArbiter.Importance.Command,
        CancellationToken ct = default) =>
        KeepQuarryingAsync(Plan(topCentre, size, depth), resume, level, ct);

    /// <summary>Ход работы: держим счётчики в одном месте, чтобы отчёт не врал.</summary>
    private sealed class Run(QuarryPlan plan, Dictionary<string, int> skips)
    {
        public readonly QuarryPlan Plan = plan;
        public readonly Dictionary<string, int> Skips = skips;
        public QuarryStop Stop = QuarryStop.Done;
        public string Reason = "план выполнен";
        public DateTime Deadline = DateTime.MaxValue;
        public int Broken, Isolated, AutoDropped, Skipped;
        public BlockPos? Last;

        /// <summary>
        /// Клетки, которых в мире уже нет. Копится ради возврата к прерванной
        /// работе: без этого списка бот на втором заходе честно шёл бы по всему
        /// плану заново — 720 клеток, из которых 67 уже выкопаны.
        /// </summary>
        public readonly List<BlockPos> Done = [];

        /// <summary>
        /// ПОСЛЕДНЯЯ ЖИВАЯ ПОМЕХА — та, что мешала, ПОКА ШЛИ ЧАСЫ, И ПРОХОДИТ
        /// САМА.
        ///
        /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНА. Игрок встал на клетку, карьер
        /// весь свой срок обходил слой и складывал пропуски («стою на ней,
        /// отойти некуда»), а в отказ выходило одно «вышло отведённое время» —
        /// то есть слово из ПОСТОЯННЫХ. Помеха, которая проходит сама,
        /// пряталась за симптомом (см. <see cref="Resume.TimeIsNotACause"/>).
        ///
        /// «И ПРОХОДИТ САМА» — НЕ ПРИДИРКА, А ВТОРАЯ ПОЛОВИНА ТОГО ЖЕ СЛУЧАЯ.
        /// Пропуск («клетку обошёл и работаю дальше») клал сюда и ПОСТОЯННЫЕ
        /// слова, и тогда срок докладывал не помеху вместо часов, а пережитое
        /// вместо срока: разбор и живой итог — у <see cref="Skip"/> и
        /// <see cref="Resume.LivedThrough"/>.
        ///
        /// Своего суждения тут нет: слово кладёт то место, которое помеху и
        /// видело, а перевод отказа клетки в общее слово берётся готовым —
        /// <see cref="Roads.WhatCellSaid"/>, второй копии ему не заводим.
        /// </summary>
        public Setback Live = Setback.None;

        /// <summary>
        /// КЛЕТКУ ОБОШЛИ И РАБОТАЕМ ДАЛЬШЕ.
        ///
        /// ПРОПУСК КЛАДЁТ В ЖИВУЮ ПОМЕХУ НЕ ВСЁ, ЧТО УВИДЕЛ, А ТОЛЬКО
        /// ПРОХОДЯЩЕЕ САМО, и решает это не карьер, а общее чистое правило
        /// (<see cref="Resume.LivedThrough"/>). Разбор — там же; здесь только
        /// живой итог, названный приёмкой 09.09: карьер на 720 клеток,
        /// обошедший ОДНУ клетку у воды на первой минуте и упёршийся в срок на
        /// десятой, докладывал <see cref="Setback.Deadly"/> — «дальше опасно
        /// для жизни, тут нужно другое место» — и второго захода не получал,
        /// хотя нужно ему было ОДНО ВРЕМЯ.
        ///
        /// ОБОЙДЁННОЕ НЕ ПРОПАДАЕТ: сама причина пропуска считается в
        /// <see cref="Skips"/> («вода рядом», «подойти нечем») и стоит в
        /// отчёте, который человек и читает. Пропадает ровно одно — право
        /// пережитой помехи назваться ПРИЧИНОЙ ОСТАНОВКИ.
        ///
        /// А ЧТО РАБОТУ ОСТАНОВИЛО, кладёт <see cref="Halt"/>, и он один на
        /// прогон: ровно поэтому у штольни этой беды нет вовсе.
        ///
        /// КУДА ЭТО СЛОВО ВЕДЁТ У КАРЬЕРА, А КУДА НЕ ВЕДЁТ — СКАЗАНО ЗДЕСЬ,
        /// ЧТОБЫ ЧИТАТЕЛЬ НЕ ЖДАЛ ВТОРОГО ЗАХОДА. Половина правила, которая
        /// ВЫБРАСЫВАЕТ пережитое постоянное слово, работает и ради неё всё и
        /// затевалось: без неё отказ по сроку говорил бы «дальше опасно для
        /// жизни» про обойдённую клетку у воды. А вот половина, которая
        /// СОХРАНЯЕТ проходящее слово ради повтора, у карьера пуста: срок тут
        /// один на всю работу, и к тому мигу, когда слово дойдёт до возврата,
        /// времени на повтор уже нет по устройству
        /// (<see cref="Resume.WithinBudget"/>, разбор — у
        /// <see cref="KeepQuarryingAsync(QuarryPlan, Resume, BodyArbiter.Importance,
        /// CancellationToken)"/>).
        /// </summary>
        /// <param name="live">
        /// что помеху ВИДЕЛО, то её и называет — своего суждения у пропуска
        /// нет. Слово может оказаться и временным (игрок на клетке —
        /// <see cref="Setback.Crowd"/>, недоехавший чанк), и постоянным; какое
        /// из них доживёт до отказа, решает правило, а не место вызова
        /// </param>
        public void Skip(string why, BlockPos at, Setback live = Setback.None)
        {
            Skipped++;
            Last = at;
            Skips[why] = Skips.GetValueOrDefault(why) + 1;
            var доживёт = Resume.LivedThrough(live);
            if (доживёт != Setback.None)
                Live = доживёт;
        }

        /// <summary>
        /// Счёт за подход: и сколько лишних блоков сломано, и за какую клетку.
        /// Заводится один на всю работу — предел «жертв за клетку» иначе
        /// ограничивал бы не клетку, а подступ к ней.
        /// </summary>
        public readonly SacrificeLedger Sacrifice = new();

        /// <summary>Сколько блоков за границей плана сломано ради доступа.</summary>
        public int Sacrificed => Sacrifice.Sacrificed;

        private HashSet<BlockPos>? harvestSet;
        private HashSet<BlockPos>? clearSet;
        private HashSet<BlockPos>? rampSet;

        /// <summary>Клетка помечена как «целый блок» — её беречь.</summary>
        public bool IsHarvestCell(BlockPos c) =>
            (harvestSet ??= [.. Plan.Harvest]).Contains(c);

        /// <summary>
        /// Клетка вообще есть в плане. Нужно жертве: сломав СВОЮ клетку раньше
        /// срока, мы делаем работу, а сломав чужую (за границей области) —
        /// платим за подход. Считать это одинаково значит врать в отчёте.
        /// </summary>
        public bool IsPlanCell(BlockPos c) =>
            IsHarvestCell(c) || (clearSet ??= [.. Plan.Clear]).Contains(c);

        /// <summary>Клетка — ступень пандуса: это дорога наверх, её не трогать.</summary>
        public bool IsRampCell(BlockPos c) =>
            (rampSet ??= [.. Plan.Ramp]).Contains(c);

        public void Halt(QuarryStop stop, string reason, BlockPos at,
            Setback live = Setback.None)
        {
            Stop = stop;
            Reason = reason;
            Last = at;
            if (live != Setback.None)
                Live = live;
        }
    }

    /// <summary>
    /// ПОМЕХА ВРЕМЕННАЯ ИЛИ ПОСТОЯННАЯ — СЛОВАМИ КАРЬЕРА, ПЕРЕВЕДЁННЫМИ В
    /// ОБЩИЕ (<see cref="Setback"/>). ЧИСТОЕ ПРАВИЛО.
    ///
    /// ЗАЧЕМ ПЕРЕВОД, А НЕ ОДИН СЛОВАРЬ НА ВСЕХ — разобрано у дороги
    /// (<see cref="Roads.WhatStopped"/>): <see cref="QuarryStop"/> отвечает на
    /// «чем кончился ИМЕННО ЭТОТ карьер» и нужен его отчёту, общее слово — на
    /// «пройдёт ли это само», и по нему решает возврат.
    ///
    /// ЖИВОЙ СЛУЧАЙ, С КОТОРОГО ВСЁ НАЧАЛОСЬ: «!карьер 7 15», 67 клеток из
    /// 720 — и тишина. Возврат к прерванному завели тогда же, а вот САМ
    /// карьер причину своей остановки называть не умел: любая его остановка
    /// приходила наверх словом <see cref="Setback.None"/>, то есть «причину не
    /// назвали», а оно лежит среди постоянных — сдача с первого раза.
    /// </summary>
    /// <param name="lastLive">
    /// Последняя ЖИВАЯ помеха карьера (<see cref="Run.Live"/>). Нужна ровно
    /// сроку: см. <see cref="Resume.TimeIsNotACause"/>.
    /// </param>
    public static Setback WhatStopped(QuarryStop stop, Setback lastLive = Setback.None) =>
        stop switch
        {
            QuarryStop.Done => Setback.None,

            // Реестр блоков сервера не пришёл — состояние связи, проходит само
            QuarryStop.NotReady => Setback.NotLoaded,

            QuarryStop.Cancelled => Setback.Stopped,

            // СРОК ДОКЛАДЫВАЕТ ПОМЕХУ, А НЕ ВСТАЁТ НА ЕЁ МЕСТО
            QuarryStop.Timeout => Resume.TimeIsNotACause(lastLive),

            // Нет кирки нужного тира, кирка кончается и запасной нет —
            // кончился запас у бота, сам он не заведётся
            QuarryStop.NoTool => Setback.NoMaterial,
            QuarryStop.ToolWorn => Setback.NoMaterial,

            // ДАЛЬШЕ УБЬЁТ: лава у клетки и осыпающийся свод над головой.
            // Ждать их бессмысленно, лезть дальше смертельно — ради этого
            // случая слово Deadly и заведено
            QuarryStop.Lava => Setback.Deadly,
            QuarryStop.Unstable => Setback.Deadly,

            // ИЗ ЯМЫ НЕ ВЫБРАТЬСЯ — ДВЕ РАЗНЫЕ ПРИЧИНЫ ПОД ОДНИМ СЛОВОМ:
            // «строить выход не велено» (запрет роли) и «нечем подниматься»
            // (кончился запас). Различает их место отказа, оно и кладёт
            // живое слово; своего суждения тут нет
            QuarryStop.NoEscape => lastLive,

            // «КЛАСТЬ НЕКУДА» (InventoryFull) — ЧЕСТНОГО ИМЕНИ НЕТ. Сумка сама
            // не опустеет, но и «бросай» тут неправда: лечится это рейсом на
            // склад, то есть ответом «устрани и вернись», которого в словаре
            // нет вовсе. NoMaterial — про кончившийся запас, а тут запаса
            // СЛИШКОМ много; подставить его значило бы соврать человеку именем
            // причины, и чинил бы он не то. Отдаём «причину не назвали», а
            // сама причина стоит словами в Reason: «в сумке не осталось
            // свободных слотов»
            QuarryStop.InventoryFull => Setback.None,

            _ => Setback.None
        };

    private QuarryReport Finish(Run state, QuarryStop stop, string reason, BlockPos at,
        Dictionary<string, int> gained, int spent, DateTime started,
        Setback live = Setback.None)
    {
        int whole = 0, rubble = 0;
        foreach (var (code, count) in gained)
        {
            if (DropsWholeWhenFloating(code))
                whole += count;
            else
                rubble += count;
        }
        var report = new QuarryReport(stop, reason, at, state.Plan.Total, state.Broken,
            state.Isolated, state.AutoDropped, state.Skipped, whole, rubble, spent,
            (Now() - started).TotalSeconds, gained, state.Skips)
        {
            Done = state.Done,
            Sacrificed = state.Sacrificed,
            // Помеху общим словом считает чистое правило, и считает ЗДЕСЬ — в
            // одном месте на все выходы из карьера (тот же разбор, что у
            // Roads.BuildAsync: расставь его по выходам, и один забудут)
            Hindrance = WhatStopped(stop, live != Setback.None ? live : state.Live)
        };
        OnLog?.Invoke(report.ToString());
        return report;
    }

    /// <summary>Что прибавилось в сумках (убыль не считаем — это расход инструмента и еды).</summary>
    private static Dictionary<string, int> Diff(Dictionary<string, int> before,
        Dictionary<string, int> after)
    {
        var gained = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, count) in after)
        {
            int delta = count - before.GetValueOrDefault(code);
            if (delta > 0)
                gained[code] = delta;
        }
        return gained;
    }

    /// <summary>
    /// Пройти один слой одного списка клеток. whole = true — это сбор целых
    /// блоков: клетку ломают, только если она ДЕЙСТВИТЕЛЬНО изолирована
    /// (проверка по правилам игры прямо перед ударом).
    /// </summary>
    private async Task MineLayerAsync(Run state, IReadOnlyList<BlockPos> cells, int y, bool whole,
        CancellationToken ct)
    {
        var left = cells.Where(c => c.Y == y).ToList();
        if (left.Count == 0)
            return;

        // ДВА ПРОХОДА — И ЭТО ТОЖЕ ПРО ДОСЯГАЕМОСТЬ.
        //
        // Внутри слоя клетки открывают друг друга: пока сосед на месте, до
        // клетки за ним не дотянуться, а сломав соседа, мы открываем её сразу.
        // Поэтому клетка, к которой подойти НЕЧЕМ, не пропускается сгоряча, а
        // откладывается до конца слоя: к тому времени соседи упали, и подход
        // появился сам. Второй проход — последний: если и теперь подойти
        // нечем, это честный отказ, а не бесконечный круг.
        for (int pass = 1; pass <= 2 && left.Count > 0; pass++)
        {
            bool last = pass == 2;
            var later = new List<BlockPos>();
            // ПОРЯДОК ОБХОДА — общий механизм добычи: от ближайшей к ногам, не
            // бросая начатую кучу и забирая тупики, пока стоим рядом. Прежняя
            // сортировка «по расстоянию от ног» считалась ОДИН РАЗ на входе в
            // слой, а бот, сломав первую клетку, уже сдвинулся — к третьей
            // порядок врал
            foreach (var cell in Mining.NearestFirst(Feet ?? new BlockPos(0, y, 0), left))
            {
                switch (await MineCellAsync(state, cell, whole, last, ct))
                {
                    case CellOutcome.Halt:
                        return;
                    case CellOutcome.Later:
                        later.Add(cell);
                        break;
                }
            }
            left = later;
        }
    }

    /// <summary>Чем кончилась попытка взять одну клетку.</summary>
    private enum CellOutcome
    {
        /// <summary>Разобрались: сломали, нашли пустой или честно пропустили.</summary>
        Handled,
        /// <summary>Подойти нечем, но соседи ещё упадут — вернёмся к ней в конце слоя.</summary>
        Later,
        /// <summary>Работа встала: идти дальше по слою незачем.</summary>
        Halt
    }

    /// <summary>
    /// Взять одну клетку. <paramref name="last"/> — это последний проход по
    /// слою: откладывать больше некуда, и «подойти нечем» надо сказать вслух.
    /// </summary>
    private async Task<CellOutcome> MineCellAsync(Run state, BlockPos cell, bool whole, bool last,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            state.Halt(QuarryStop.Cancelled, "отменено", cell);
            return CellOutcome.Halt;
        }
        // ЕДИНСТВЕННЫЙ ВОПРОС О ВРЕМЕНИ ВНУТРИ РАБОТЫ, И ОН ЖЕ ДЕЛАЕТ ЧАСЫ
        // карьера часами РАБОТЫ, а не стены: один вопрос — одна клетка
        // (см. <see cref="Now"/>)
        if (Now() > state.Deadline)
        {
            state.Halt(QuarryStop.Timeout, $"вышло отведённое время ({MaxSeconds:0} с)", cell);
            return CellOutcome.Halt;
        }
        if (StopWhenFull && FreeSlots() == 0)
        {
            state.Halt(QuarryStop.InventoryFull, "в сумке не осталось свободных слотов", cell);
            return CellOutcome.Halt;
        }

        int id = ctx.World.GetBlockId(cell.X, cell.Y, cell.Z);
        if (id == 0)
        {
            // Пусто. Для клеток сбора это ожидаемо: если на сервере включён
            // allowFallingBlocks, BreakIfFloating.OnNeighbourBlockChange сам
            // ломает повисший блок, и целый блок уже лежит на земле
            state.Done.Add(cell);
            if (whole)
            {
                state.AutoDropped++;
                state.Last = cell;
            }
            return CellOutcome.Handled;
        }

        if (whole && !IsFloating(cell))
        {
            // Не изолирован — ломать сейчас значит получить крошку вместо
            // блока. Оставляем: пусть лучше камень стоит
            // «НЕ ИЗОЛИРОВАН» — ЭТО НЕ ПОМЕХА ВОВСЕ, А ПОРЯДОК РАБОТ: клетку
            // возьмут, когда уберут слой снизу. Живым словом её не помечаем —
            // иначе срок доложил бы «мне мешали» там, где карьер спокойно шёл
            // по плану
            state.Skip("не изолирован", cell);
            return CellOutcome.Handled;
        }

        if (DangerNear(cell) is { } danger)
        {
            if (danger.Lava && StopOnLava)
            {
                state.Halt(QuarryStop.Lava, $"лава в {danger.At} — дальше не копаю", cell);
                return CellOutcome.Halt;
            }
            // КЛЕТКУ ОБОШЛИ, И СЛОВО ЕЙ — «СМЕРТЕЛЬНО». Называем то, что
            // увидели, и на этом наше дело кончается: доживёт ли слово до
            // отказа, решает не забой, а правило пережитой помехи
            // (Resume.LivedThrough у Run.Skip). Здесь оно не доживёт — карьер
            // обошёл эту клетку и работал ДАЛЬШЕ, а «дальше опасно для жизни,
            // тут нужно другое место» сказано про место, где 719 клеток из 720
            // стоят себе спокойно
            state.Skip(danger.Lava ? "лава рядом" : "вода рядом", cell, Setback.Deadly);
            return CellOutcome.Handled;
        }

        if (Feet is { } f && (cell == f || cell == new BlockPos(f.X, f.Y - 1, f.Z)))
        {
            // Клетка ПОД НОГАМИ. Живой игрок её просто ломает и падает на
            // блок ниже — это обычная копка вглубь, а не происшествие.
            // Бот же отходил в сторону, а в вырытой яме отходить некуда,
            // и клетка уходила в пропуск: «стою на ней, отойти некуда».
            bool underFoot = cell == new BlockPos(f.X, f.Y - 1, f.Z);
            if (!(underFoot && DigUnderSelf && SafeToDropInto(cell)) &&
                !await StepAsideAsync(cell, ct))
            {
                // «СТОЮ НА НЕЙ, ОТОЙТИ НЕКУДА» — это мир вокруг тела, и он
                // переменится: тот же случай и то же слово, каким дорога
                // читает MiningRefusal.SelfInTheWay
                state.Skip("стою на ней, отойти некуда", cell, Setback.Crowd);
                return CellOutcome.Handled;
            }
        }

        if (CareAboutCeiling && Feet is { } stand && !CeilingSafeAt(stand) &&
            !await MoveOutFromUnderCeilingAsync(ct))
        {
            state.Halt(QuarryStop.Unstable,
                "над головой нестабильная порода, безопасного места рядом нет", cell);
            return CellOutcome.Halt;
        }

        // ЧЕМ БРАТЬ — СПРАШИВАЕМ РАНЬШЕ, ЧЕМ КУДА ВСТАТЬ. Без кирки нужного
        // тира не сломать ни саму клетку, ни соседа ради подхода: начни мы с
        // подхода, каждая клетка ушла бы в «подойти нечем», и настоящая
        // причина — «нет кирки» — не прозвучала бы ни разу
        if (ctx.World.RequiredMiningTier(cell.X, cell.Y, cell.Z) > 0)
        {
            if (ctx.Mining.BestToolFor(cell) is not { } tool)
            {
                state.Halt(QuarryStop.NoTool,
                    $"{ctx.World.GetBlockCode(cell)} нечем брать — нет кирки нужного тира", cell);
                return CellOutcome.Halt;
            }
            if (ctx.Wear.Remaining(tool.Content) is { } worn && worn < MinToolDurability &&
                ctx.Wear.FindSpare(tool.Content.Code, MinToolDurability,
                    tool.InventoryId, tool.Slot) == null)
            {
                state.Halt(QuarryStop.ToolWorn,
                    $"у {tool.Content.Code} осталось {worn} прочности, запасной нет", cell);
                return CellOutcome.Halt;
            }
        }

        // ЕСТЬ ЛИ ВООБЩЕ ОТКУДА ДОТЯНУТЬСЯ — СПРАШИВАЕМ ДО ТОГО, КАК ЗВАТЬ
        // ДОРОГУ. Ровно этого вопроса и не хватало: план выдавал клетку,
        // подхода к которой нет вовсе, добыча честно говорила «не дотянуться»,
        // а движение принималось искать путь туда, куда пути и быть не может —
        // «не хватает 2 бл по высоте, и ближе не становится», десятки раз в
        // секунду. Теперь: нет подхода — сперва снимаем ТО, ЧТО МЕШАЕТ
        // ПОДОЙТИ, и только потом идём
        if (!CanBeReached(cell) &&
            !await OpenAccessAsync(state, cell, ct) && !CanBeReached(cell))
        {
            if (!last)
                return CellOutcome.Later;
            state.Skip("подойти нечем", cell, Setback.OutOfReach);
            return CellOutcome.Handled;
        }

        var result = await ctx.Mining.BreakAsync(cell, ct);
        if (!result.Success && result.Why == MiningRefusal.OutOfReach &&
            await ComeCloserAsync(cell, ct))
            result = await ctx.Mining.BreakAsync(cell, ct);

        // Подойти не вышло — прокапываемся, жертвуя лишним
        if (!result.Success && result.Why == MiningRefusal.OutOfReach &&
            await OpenAccessAsync(state, cell, ct))
            result = await ctx.Mining.BreakAsync(cell, ct);

        if (result.Success)
        {
            state.Broken++;
            state.Last = cell;
            state.Done.Add(cell);
            if (whole)
                state.Isolated++;
            return CellOutcome.Handled;
        }

        // «Не дотянулся» на первом проходе — это «рано», а не «нельзя»:
        // соседи по слою ещё упадут и откроют клетку сами
        if (!last && result.Why == MiningRefusal.OutOfReach)
            return CellOutcome.Later;

        // ЧТО СКАЗАЛА КЛЕТКА — ОБЩИМ СЛОВОМ, И ПРАВИЛО ОДНО НА ВЕСЬ ПРОЕКТ
        // (Roads.WhatCellSaid). Второй разбор MiningRefusal здесь разошёлся бы
        // с первым на первой же правке словаря добычи
        state.Skip(result.Why.ToString(), cell, Roads.WhatCellSaid(result.Why));
        return CellOutcome.Handled;
    }
}
