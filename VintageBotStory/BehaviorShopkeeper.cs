using VsBotKit;

namespace VintageBotStory;

/// <summary>
/// Продавец за прилавком: сам обходит сундуки магазина, проводит сделки и
/// возвращается на пост. Без этой способности торговля работала только по
/// ручной команде — игрок кладёт деньги, а бот стоит и ничего не делает.
/// </summary>
public class BehaviorShopkeeper : BotBehavior
{
    private readonly BotContext ctx;
    private readonly TraderShop shop;

    public BehaviorShopkeeper(BotContext ctx, TraderShop shop)
    {
        this.ctx = ctx;
        this.shop = shop;
    }

    /// <summary>
    /// КАК ЭТА СПОСОБНОСТЬ ЗОВЁТСЯ ЧЕЛОВЕКУ. Тем же словом он двигает её вес в
    /// строке «ВесаВажности» («лавка 60; еда 80») и по нему же находит её в
    /// списке важности в окне. Библиотечная таблица имён назвать её не может —
    /// она в VsBotKit, а роль в приложении (см. <see cref="BotBehavior.Title"/>);
    /// без этой строки продавец называл себя человеку «Shopkeeper».
    /// </summary>
    public override string Title => "лавка";

    /// <summary>
    /// ЛАВКА, КОТОРУЮ ЭТОТ ПРОДАВЕЦ ОБСЛУЖИВАЕТ. Только на чтение и нарочно:
    /// подменить лавку на ходу значит получить бота, который стоит у одного
    /// прилавка, а торгует другим.
    ///
    /// Наружу нужна затем, что торговые числа (радиус магазина, код монеты,
    /// рейсы за сделку) — это ручки РОЛИ, и добраться до механизма, которому их
    /// доносят, надо и роли, и стенду. Способность бот отдаёт сам
    /// (<c>bot.Behaviors.Get&lt;BehaviorShopkeeper&gt;()</c>), а через неё
    /// открывается и лавка — второго пути заводить незачем.
    /// </summary>
    public TraderShop Shop => shop;

    /// <summary>
    /// ПРЕДОХРАНИТЕЛЬ. Пока перенос предметов не переписан, бот только
    /// ОСМАТРИВАЕТ прилавки и докладывает, но сделок НЕ проводит: текущий
    /// перенос умеет платить дважды за одно и то же и вымывает кассу.
    /// Включать, когда перенос станет атомарным и проверяемым.
    /// </summary>
    public bool ExecuteDeals { get; set; }

    /// <summary>Как часто обходить прилавки.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Пост: где стоять между обходами. По умолчанию — у таблички «!STORE»,
    /// то есть ВНУТРИ магазина. Дальше этого расстояния бот не отходит,
    /// разве что за товаром на склад.
    /// </summary>
    public double PostRadius { get; set; } = 3.0;

    /// <summary>
    /// Дальше этого от поста бот САМ не возвращается: это «унесло», а не
    /// «работа». За товаром на склад он ходит и дальше.
    ///
    /// ЗНАЧИТ РОВНО ТО, ЧТО НАПИСАНО. Здесь поводок УДВАИВАЛСЯ прямо в проверке,
    /// то есть человек, поставивший «поводок 25», получал бота, уходящего за
    /// пятьдесят. Мерка и приговор теперь спрашиваются у общего правила
    /// (<see cref="PostRules"/>), и удваивать там некому.
    /// </summary>
    public double LeashRadius { get; set; } = 25.0;

    public event Action<string>? OnLog;

    private DateTime nextRound = DateTime.MinValue;
    private DateTime nextPostTry = DateTime.MinValue;
    private bool postComplained;

    /// <summary>Как часто пробовать вернуться на пост, если не дошёл.</summary>
    public TimeSpan PostRetry { get; set; } = TimeSpan.FromSeconds(20);

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (ctx.Movement.IsBusy || ctx.Entities.Self is not { } self)
            return false;

        // Пост — клетка внутри магазина, рядом с табличкой «!STORE».
        // Считает его сам магазин: он же проверяет, что там есть опора,
        // иначе бот пытался встать в воздух (баг из раздела 3)
        var post = shop.PostCell();

        // Сначала обход: если игрок положил деньги или товар, это важнее
        if (DateTime.UtcNow >= nextRound)
        {
            nextRound = DateTime.UtcNow + Interval;
            var deals = await shop.SurveyAsync(ct);
            if (deals.Count > 0)
            {
                if (!ExecuteDeals)
                {
                    OnLog?.Invoke($"вижу сделок: {deals.Count}, но торговля выключена " +
                                  "предохранителем (перенос предметов переписывается)");
                    return true;
                }
                foreach (var deal in deals)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    await shop.ExecuteAsync(deal, ct);
                }
                return true; // сделки заняли ход
            }

            // Сделок нет — самое время посмотреть, не пуста ли касса:
            // магазин без шестерён встанет насовсем
            if (await shop.MaintainCashAsync(ct))
                return true;
        }

        // Дел нет — стоим внутри магазина, а не там, где нас оставили.
        // ВАЖНО: возврат на пост НЕ ДОЛЖЕН съедать каждый ход. Раньше он
        // повторялся каждую секунду, не доходил и тем самым не давал
        // состояться торговле — бот «ни покупал, ни продавал»
        if (post is not { } p || DateTime.UtcNow < nextPostTry)
            return false;

        // МЕРКА И ПРИГОВОР — ОБЩИЕ (PostRules), А НЕ СВОИ. Здесь лежала вторая
        // копия правила поста: она мерила без высоты (прилавок плоский, цена
        // ошибки нулевая) и отпускала бота на ДВА поводка. Пока копия была
        // одна, это было почти безобидно; с приходом стражника одно правило
        // оказалось написано в проекте трижды, и разойтись им было где
        double away = PostRules.Отступ(self.X, self.Y, self.Z, p);
        if (PostRules.Что(естьПост: true, away, PostRadius, LeashRadius)
            != PostVerdict.Вернуться)
            return false;

        nextPostTry = DateTime.UtcNow + PostRetry;
        if (!postComplained)
            OnLog?.Invoke($"возвращаюсь на пост в магазин ({away:0.#} бл)");
        bool ok = await ctx.Movement.MoveToCellAsync(p, ct);
        if (!ok && !postComplained)
        {
            postComplained = true;
            OnLog?.Invoke($"на пост {p} не пройти — торгую с места, где стою");
        }
        else if (ok)
        {
            postComplained = false;
        }
        return true;
    }

    // Пост считает сам магазин (TraderShop.PostCell): это его правило, а не
    // способности, и там же проверяется опора под ногами
}
