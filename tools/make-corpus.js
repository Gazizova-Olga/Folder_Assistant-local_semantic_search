'use strict';
// Generates a multilingual test corpus for Folder Assistant.
//
// Sizes are computed against the real chunk rule: a 256-token window stepping 224 tokens, where a
// token is a run of non-space characters (SimpleTokenizer). A file of 256 + 224k tokens ends on a
// full window; anything else leaves a short trailing chunk.
//
// Usage: node make-corpus.js <output-directory>

const fs = require('fs');
const path = require('path');

const OUT = process.argv[2];
if (!OUT) { console.error('usage: node make-corpus.js <output-directory>'); process.exit(1); }

const CHUNK = 256, STEP = 224;
const exactSizes = [256, 480, 704, 1152, 2272];              // whole number of chunks
const partialSizes = [90, 300, 600, 1000, 1800, 3000];        // trailing short chunk

function chunkCount(n) {
  if (n === 0) return 0;
  let chunks = 0, lastEnd = 0;
  for (let start = 0; start < n; start += STEP) {
    const end = Math.min(n, start + CHUNK);
    if (chunks > 0 && end <= lastEnd) break;
    chunks++; lastEnd = end;
  }
  return chunks;
}
function lastChunkTokens(n) {
  let lastStart = 0, lastEnd = 0, chunks = 0;
  for (let start = 0; start < n; start += STEP) {
    const end = Math.min(n, start + CHUNK);
    if (chunks > 0 && end <= lastEnd) break;
    chunks++; lastStart = start; lastEnd = end;
  }
  return lastEnd - lastStart;
}

// ── Topics ───────────────────────────────────────────────────────────────────────────────────
// Each topic carries real sentences in three languages, one of which states a distinctive fact the
// labelled queries ask about without reusing its words.
const topics = [
  {
    id: 'beekeeping',
    en: { title: 'Hive inspection notes',
      lines: ['The colony is opened every nine days during the main flow, never more often.',
        'Smoke is applied at the entrance first and then under the lid, two puffs at most.',
        'A queen that has stopped laying is replaced before the end of July, not in autumn.',
        'Frames are lifted from the outside inward so the brood nest stays covered.',
        'Winter stores below twelve kilograms mean feeding syrup until the weight is reached.'] },
    ru: { title: 'Записи об осмотре улья',
      lines: ['Семью открывают каждые девять дней во время главного взятка, не чаще.',
        'Дым пускают сначала в леток, затем под крышку, не больше двух клубов.',
        'Матку, переставшую червить, меняют до конца июля, а не осенью.',
        'Рамки поднимают с краю к середине, чтобы гнездо оставалось прикрытым.',
        'Кормовые запасы ниже двенадцати килограммов означают подкормку сиропом до нужного веса.'] },
    tr: { title: 'Kovan muayene notları',
      lines: ['Ana nektar akışında koloni dokuz günde bir açılır, daha sık değil.',
        'Duman önce uçuş deliğine, sonra kapağın altına verilir, en fazla iki püskürtme.',
        'Yumurtlamayı bırakan ana arı temmuz sonundan önce değiştirilir, sonbaharda değil.',
        'Çerçeveler dıştan içe doğru kaldırılır, böylece yavrulu alan örtülü kalır.',
        'On iki kilogramın altındaki kışlık yem, gereken ağırlığa kadar şurup vermek demektir.'] },
    queries: {
      en: 'how often should I open the bees in summer',
      ru: 'как часто заглядывать к пчёлам летом',
      tr: 'yazın arılara ne sıklıkla bakmalıyım',
      cross: 'when is a non-laying queen replaced' },
  },
  {
    id: 'ferry',
    en: { title: 'Island ferry timetable',
      lines: ['The first crossing leaves the mainland pier at 07:40 and takes fifty minutes.',
        'Vehicles board twenty minutes before departure; foot passengers ten minutes before.',
        'In a southwesterly above force six the afternoon sailing is cancelled outright.',
        'Tickets bought at the pier cost the same as online ones, with no reservation.',
        'The last return leaves the island at 19:15 in summer and 17:15 in winter.'] },
    ru: { title: 'Расписание парома на остров',
      lines: ['Первый рейс отходит от материкового причала в 07:40 и идёт пятьдесят минут.',
        'Машины загружаются за двадцать минут до отхода, пешие пассажиры за десять.',
        'При юго-западном ветре выше шести баллов дневной рейс отменяют полностью.',
        'Билеты на причале стоят столько же, сколько онлайн, но без брони места.',
        'Последний обратный рейс уходит с острова в 19:15 летом и в 17:15 зимой.'] },
    tr: { title: 'Ada vapur tarifesi',
      lines: ['İlk sefer anakara iskelesinden 07:40’ta kalkar ve elli dakika sürer.',
        'Araçlar kalkıştan yirmi dakika önce, yaya yolcular on dakika önce biner.',
        'Altı kuvvetin üzerindeki lodosta öğleden sonraki sefer tamamen iptal edilir.',
        'İskeleden alınan bilet, internetten alınanla aynı fiyattır, yer ayırtmadan.',
        'Adadan son dönüş yazın 19:15’te, kışın 17:15’te kalkar.'] },
    queries: {
      en: 'what time does the earliest boat go out',
      ru: 'во сколько уходит самый ранний катер',
      tr: 'en erken tekne kaçta kalkıyor',
      cross: 'which weather stops the afternoon sailing' },
  },
  {
    id: 'sourdough',
    en: { title: 'Sourdough starter routine',
      lines: ['The starter is fed twice a day at a ratio of one to five to five by weight.',
        'A grey liquid on top means it is hungry, not spoiled, and is stirred back in.',
        'Bulk fermentation ends when the dough has risen by three quarters, never doubled.',
        'The oven is preheated with the pot inside for forty-five minutes at 250 degrees.',
        'Loaves are cooled on a rack for at least two hours before the first cut.'] },
    ru: { title: 'Уход за ржаной закваской',
      lines: ['Закваску кормят дважды в день в пропорции один к пяти к пяти по весу.',
        'Серая жидкость сверху означает голод, а не порчу, и её размешивают обратно.',
        'Брожение теста заканчивают, когда оно поднялось на три четверти, а не вдвое.',
        'Духовку прогревают вместе с казаном сорок пять минут при 250 градусах.',
        'Хлеб остужают на решётке не меньше двух часов до первого разреза.'] },
    tr: { title: 'Ekşi maya bakımı',
      lines: ['Maya günde iki kez, ağırlıkça bire beşe beş oranında beslenir.',
        'Üstteki gri sıvı bozulma değil açlık demektir ve karıştırılarak geri alınır.',
        'Ana fermantasyon hamur dörtte üç kabardığında biter, iki katına çıkınca değil.',
        'Fırın, tencere içindeyken 250 derecede kırk beş dakika ısıtılır.',
        'Ekmekler ilk kesimden önce en az iki saat tel ızgarada soğutulur.'] },
    queries: {
      en: 'the dark layer on top of my culture, is it ruined',
      ru: 'тёмный слой сверху закваски это конец',
      tr: 'mayamın üstündeki koyu sıvı bozulmuş mu',
      cross: 'how long before slicing the bread' },
  },
  {
    id: 'greenhouse',
    en: { title: 'Greenhouse irrigation log',
      lines: ['Drip lines run for eleven minutes at dawn and again at four in the afternoon.',
        'Soil is checked at a depth of two fingers; damp at that depth means skip a cycle.',
        'Salt crusting at the emitters is flushed with clean water every second week.',
        'Tomatoes get a tenth of the potassium feed once the third truss has set.',
        'Vents open automatically above twenty-eight degrees and close below eighteen.'] },
    ru: { title: 'Журнал полива теплицы',
      lines: ['Капельные линии работают одиннадцать минут на рассвете и снова в четыре дня.',
        'Почву проверяют на глубину двух пальцев: если там влажно, цикл пропускают.',
        'Солевой налёт на капельницах промывают чистой водой каждые две недели.',
        'Томатам дают десятую долю калийной подкормки после завязи третьей кисти.',
        'Форточки открываются сами выше двадцати восьми градусов и закрываются ниже восемнадцати.'] },
    tr: { title: 'Sera sulama kaydı',
      lines: ['Damla hatları şafakta on bir dakika, öğleden sonra dörtte yine çalışır.',
        'Toprak iki parmak derinlikte kontrol edilir; orası nemliyse bir tur atlanır.',
        'Damlatıcılardaki tuz kabuğu iki haftada bir temiz suyla yıkanır.',
        'Üçüncü salkım bağladıktan sonra domatese potasyum gübresinin onda biri verilir.',
        'Pencereler yirmi sekiz derecenin üstünde açılır, on sekizin altında kapanır.'] },
    queries: {
      en: 'how do I know whether to water today',
      ru: 'как понять нужно ли поливать сегодня',
      tr: 'bugün sulamam gerekip gerekmediğini nasıl anlarım',
      cross: 'at what temperature do the windows open' },
  },
  {
    id: 'insurance',
    en: { title: 'Earthquake cover terms',
      lines: ['A claim must be filed within fifteen working days of the event, not thirty.',
        'Cover applies to the structure and fixed installations, never to loose contents.',
        'The deductible is two percent of the insured sum, paid before any settlement.',
        'Buildings with an unpermitted extra floor are excluded from the policy entirely.',
        'An adjuster visits within seventy-two hours in a declared disaster zone.'] },
    ru: { title: 'Условия страхования от землетрясения',
      lines: ['Заявление подают в течение пятнадцати рабочих дней после события, не тридцати.',
        'Покрытие распространяется на конструкции и стационарное оборудование, но не на движимое имущество.',
        'Франшиза составляет два процента страховой суммы и удерживается до выплаты.',
        'Здания с самовольно надстроенным этажом исключаются из полиса полностью.',
        'Оценщик приезжает в течение семидесяти двух часов в зоне объявленного бедствия.'] },
    tr: { title: 'Deprem teminatı koşulları',
      lines: ['Hasar bildirimi olaydan sonra otuz değil, on beş iş günü içinde yapılmalıdır.',
        'Teminat yapıyı ve sabit tesisatı kapsar, taşınır eşyayı kapsamaz.',
        'Muafiyet sigorta bedelinin yüzde ikisidir ve ödemeden önce düşülür.',
        'Ruhsatsız kat çıkılmış binalar poliçe kapsamı dışındadır.',
        'İlan edilmiş afet bölgesinde eksper yetmiş iki saat içinde gelir.'] },
    queries: {
      en: 'how long do I have to report the damage',
      ru: 'сколько времени есть на подачу заявления об ущербе',
      tr: 'hasarı bildirmek için ne kadar sürem var',
      cross: 'is furniture covered by this policy' },
  },
  {
    id: 'thesis',
    en: { title: 'Thesis defence regulations',
      lines: ['The manuscript is submitted eight weeks before the defence, in two bound copies.',
        'Two external reviewers are appointed, and one of them must chair no committee.',
        'The presentation lasts twenty minutes and questions run for up to an hour.',
        'A failed defence may be repeated once, no sooner than six months afterwards.',
        'Corrections are due within four weeks of the committee signing the record.'] },
    ru: { title: 'Регламент защиты диссертации',
      lines: ['Рукопись сдают за восемь недель до защиты, в двух переплетённых экземплярах.',
        'Назначают двух внешних рецензентов, один из которых не входит ни в один совет.',
        'Доклад длится двадцать минут, вопросы занимают до часа.',
        'Неудачную защиту можно повторить один раз, не раньше чем через полгода.',
        'Правки вносят в течение четырёх недель после подписания протокола.'] },
    tr: { title: 'Tez savunma yönergesi',
      lines: ['Tez, savunmadan sekiz hafta önce iki ciltli nüsha hâlinde teslim edilir.',
        'İki dış jüri üyesi atanır ve bunlardan biri hiçbir komiteye başkanlık etmemelidir.',
        'Sunum yirmi dakika sürer, sorular bir saati bulabilir.',
        'Başarısız savunma altı aydan önce olmamak üzere bir kez tekrarlanabilir.',
        'Düzeltmeler tutanağın imzalanmasından sonraki dört hafta içinde yapılır.'] },
    queries: {
      en: 'when must the manuscript be handed in',
      ru: 'когда нужно сдать рукопись',
      tr: 'tez ne zaman teslim edilmeli',
      cross: 'can I defend again after failing' },
  },
  {
    id: 'guitar',
    en: { title: 'Instrument maintenance card',
      lines: ['The truss rod is turned an eighth of a turn at a time, then left for a day.',
        'Relief is measured at the eighth fret with the first and last frets pressed.',
        'Strings are wiped dry after playing; that alone doubles how long they last.',
        'A dry room below thirty-five percent humidity will lift the fingerboard edges.',
        'Frets are levelled only after the neck is straight, never before.'] },
    ru: { title: 'Карточка обслуживания инструмента',
      lines: ['Анкерный стержень поворачивают на одну восьмую оборота и оставляют на сутки.',
        'Прогиб измеряют на восьмом ладу, прижав первый и последний лады.',
        'Струны протирают насухо после игры — одно это удваивает их срок.',
        'Сухая комната ниже тридцати пяти процентов влажности поднимает края накладки.',
        'Лады выравнивают только после того, как гриф выпрямлен, и никогда раньше.'] },
    tr: { title: 'Enstrüman bakım kartı',
      lines: ['Ayar çubuğu her seferinde sekizde bir tur çevrilir ve bir gün bekletilir.',
        'Boşluk, ilk ve son perdeler basılıyken sekizinci perdede ölçülür.',
        'Teller çalındıktan sonra kurulanır; tek başına bu, ömrünü ikiye katlar.',
        'Yüzde otuz beşin altındaki kuru oda klavye kenarlarını kaldırır.',
        'Perdeler ancak sap düzeldikten sonra tesviye edilir, asla önce değil.'] },
    queries: {
      en: 'my neck is bowed, how much should I turn the rod',
      ru: 'гриф повело насколько крутить анкер',
      tr: 'sapı eğrilmiş, çubuğu ne kadar çevirmeliyim',
      cross: 'what humidity damages the fingerboard' },
  },
  {
    id: 'vaccination',
    en: { title: 'Puppy vaccination schedule',
      lines: ['The first combined shot is given at six weeks, the second at nine.',
        'Rabies is not given before twelve weeks of age under any circumstance.',
        'A puppy stays away from public parks until a week after the third shot.',
        'Worming is repeated every two weeks until three months, then monthly.',
        'A mild fever on the day after an injection is expected and needs nothing.'] },
    ru: { title: 'График прививок щенка',
      lines: ['Первую комплексную прививку делают в шесть недель, вторую в девять.',
        'Прививку от бешенства не делают раньше двенадцати недель ни при каких условиях.',
        'Щенка не водят в общественные парки до недели после третьей прививки.',
        'Глистогонят каждые две недели до трёх месяцев, затем раз в месяц.',
        'Небольшая температура на следующий день после укола ожидаема и лечения не требует.'] },
    tr: { title: 'Yavru köpek aşı takvimi',
      lines: ['İlk karma aşı altıncı haftada, ikincisi dokuzuncu haftada yapılır.',
        'Kuduz aşısı hiçbir koşulda on ikinci haftadan önce yapılmaz.',
        'Yavru, üçüncü aşıdan bir hafta sonrasına kadar parklara götürülmez.',
        'Solucan ilacı üç aya kadar iki haftada bir, sonra ayda bir tekrarlanır.',
        'İğneden sonraki gün hafif ateş beklenen bir durumdur ve tedavi gerektirmez.'] },
    queries: {
      en: 'earliest age for the rabies injection',
      ru: 'с какого возраста можно прививать от бешенства',
      tr: 'kuduz aşısı en erken kaç haftalıkken yapılır',
      cross: 'when can the puppy meet other dogs outside' },
  },
  {
    id: 'solar',
    en: { title: 'Panel cleaning procedure',
      lines: ['Panels are washed before sunrise or after sunset, never on hot glass.',
        'Only deionised water and a soft brush are used; detergents void the warranty.',
        'Output is logged before and after so the gain from cleaning is known.',
        'Bird droppings are soaked for ten minutes rather than scraped off dry.',
        'A panel with a cracked backsheet is disconnected and not cleaned at all.'] },
    ru: { title: 'Порядок очистки панелей',
      lines: ['Панели моют до восхода или после заката, но никогда по горячему стеклу.',
        'Используют только деионизированную воду и мягкую щётку: моющие средства аннулируют гарантию.',
        'Выработку записывают до и после, чтобы знать прирост от мойки.',
        'Птичий помёт размачивают десять минут, а не соскабливают насухо.',
        'Панель с треснувшей подложкой отключают и не моют вовсе.'] },
    tr: { title: 'Panel temizleme yordamı',
      lines: ['Paneller gün doğmadan önce ya da battıktan sonra yıkanır, sıcak camda asla.',
        'Yalnızca saf su ve yumuşak fırça kullanılır; deterjan garantiyi geçersiz kılar.',
        'Temizliğin kazancı bilinsin diye üretim öncesi ve sonrası kaydedilir.',
        'Kuş pislikleri kuru kuruya kazınmaz, on dakika ıslatılır.',
        'Arka yüzeyi çatlamış panel sökülür ve hiç temizlenmez.'] },
    queries: {
      en: 'what time of day to wash the array',
      ru: 'в какое время суток мыть панели',
      tr: 'panelleri günün hangi saatinde yıkamalı',
      cross: 'can I use soap on the modules' },
  },
  {
    id: 'chess',
    en: { title: 'Opening repertoire notes',
      lines: ['Against the closed setup the knight goes to d2 before the bishop moves.',
        'The pawn break in the centre is delayed until the king has castled short.',
        'A queen brought out on move four invites tempo loss and is avoided.',
        'In the endgame the rook belongs behind the passed pawn, on either side.',
        'Time trouble is handled by playing the move the position asks, not the best move.'] },
    ru: { title: 'Заметки по дебютному репертуару',
      lines: ['Против закрытой схемы конь идёт на d2 раньше, чем ходит слон.',
        'Подрыв в центре откладывают до короткой рокировки короля.',
        'Ферзь, выведенный на четвёртом ходу, ведёт к потере темпа, и его не трогают.',
        'В эндшпиле ладья стоит позади проходной пешки, с любой стороны.',
        'В цейтноте играют ход, которого требует позиция, а не лучший ход.'] },
    tr: { title: 'Açılış repertuvarı notları',
      lines: ['Kapalı dizilişe karşı at, fil oynamadan önce d2’ye gider.',
        'Merkezdeki piyon kırılması kısa rok yapılana kadar ertelenir.',
        'Dördüncü hamlede çıkarılan vezir tempo kaybı getirir ve bundan kaçınılır.',
        'Oyun sonunda kale, geçer piyonun arkasında durur, iki taraftan da.',
        'Zaman sıkışıklığında en iyi hamle değil, pozisyonun istediği hamle oynanır.'] },
    queries: {
      en: 'where should the rook stand against a passer',
      ru: 'где ставить ладью против проходной',
      tr: 'geçer piyona karşı kale nerede durmalı',
      cross: 'when to break in the centre' },
  },
  {
    id: 'hut',
    en: { title: 'Mountain hut booking rules',
      lines: ['Beds are held until six in the evening and released to walk-ins after that.',
        'A cancellation later than two days before the stay is charged in full.',
        'The hut takes no cards; cash in local currency is the only payment.',
        'Boots are left in the entrance room and slippers are provided inside.',
        'Quiet hours run from ten at night until six in the morning without exception.'] },
    ru: { title: 'Правила бронирования приюта',
      lines: ['Места держат до шести вечера, после чего отдают пришедшим без брони.',
        'Отмена позже чем за двое суток до заезда оплачивается полностью.',
        'Приют не принимает карты: оплата только наличными в местной валюте.',
        'Ботинки оставляют в прихожей, внутри выдают тапочки.',
        'Тишина соблюдается с десяти вечера до шести утра без исключений.'] },
    tr: { title: 'Dağ evi rezervasyon kuralları',
      lines: ['Yataklar akşam altıya kadar tutulur, sonra gelenlere verilir.',
        'Konaklamadan iki günden geç yapılan iptal tam ücretlendirilir.',
        'Dağ evi kart almaz; ödeme yalnızca yerel para birimiyle nakittir.',
        'Botlar giriş odasında bırakılır, içeride terlik verilir.',
        'Sessizlik saatleri istisnasız gece ondan sabah altıya kadardır.'] },
    queries: {
      en: 'how do I pay when I get there',
      ru: 'чем можно расплатиться на месте',
      tr: 'orada nasıl ödeme yapabilirim',
      cross: 'until what time is my bed kept' },
  },
  {
    id: 'archive',
    en: { title: 'Paper archive handling',
      lines: ['Documents are stored flat, never folded, in boxes of acid-free board.',
        'The reading room keeps fifty percent humidity and eighteen degrees all year.',
        'Only pencil is allowed at the tables, and gloves are worn for photographs only.',
        'A file is returned to the same box in the same order it was taken from.',
        'Brittle paper is photographed rather than copied on a flatbed scanner.'] },
    ru: { title: 'Работа с бумажным архивом',
      lines: ['Документы хранят в развёрнутом виде, не складывая, в коробках из бескислотного картона.',
        'В читальном зале круглый год держат пятьдесят процентов влажности и восемнадцать градусов.',
        'За столами разрешён только карандаш, перчатки надевают лишь для фотографий.',
        'Дело возвращают в ту же коробку и в том же порядке, в каком взяли.',
        'Хрупкую бумагу фотографируют, а не копируют на планшетном сканере.'] },
    tr: { title: 'Kâğıt arşiv kullanımı',
      lines: ['Belgeler katlanmadan, asitsiz karton kutularda düz saklanır.',
        'Okuma salonunda yıl boyu yüzde elli nem ve on sekiz derece tutulur.',
        'Masalarda yalnızca kurşun kalem serbesttir, eldiven sadece fotoğraf için giyilir.',
        'Dosya alındığı kutuya, alındığı sırayla geri konur.',
        'Kırılgan kâğıt tarayıcıda kopyalanmaz, fotoğraflanır.'] },
    queries: {
      en: 'what may I write with in the reading room',
      ru: 'чем можно писать в читальном зале',
      tr: 'okuma salonunda neyle yazabilirim',
      cross: 'how should fragile sheets be reproduced' },
  },
];

// Neutral filler per language: words that carry no topic signal, so padding to an exact token count
// cannot flatter retrieval.
const filler = {
  en: ['note', 'entry', 'record', 'section', 'summary', 'detail', 'item', 'page', 'remark', 'line', 'part', 'column'],
  ru: ['запись', 'заметка', 'пункт', 'раздел', 'сводка', 'деталь', 'строка', 'страница', 'часть', 'колонка', 'абзац', 'пометка'],
  tr: ['not', 'kayıt', 'madde', 'bölüm', 'özet', 'ayrıntı', 'satır', 'sayfa', 'parça', 'sütun', 'paragraf', 'işaret'],
};

const langs = ['en', 'ru', 'tr'];

// ── Formats ──────────────────────────────────────────────────────────────────────────────────
// Each wrapper takes the body's already-tokenised words and returns file text. Wrappers add their
// own tokens, so the body is sized as (target - wrapper tokens) and the result is asserted exactly.
// Every wrapper contributes a *constant* number of tokens, whatever the body length: a token is a
// run of non-space characters, so any punctuation that would stand alone (a bare comma, a YAML
// dash, a repeated comment marker) is attached to a word or moved out of the body. Without that the
// overhead grows with the body and no file can be sized to a chunk boundary on purpose.
const wrap = {
  plain: (t, b) => `${t}\n\n${lines(b, 12)}\n`,
  md: (t, b) => `# ${t}\n\n${lines(b, 12)}\n`,
  log: (t, b) => `2026-09-24T08:00:00Z INFO ${t}\n${lines(b, 10)}\n`,
  ini: (t, b) => `; ${t}\n[notes]\ntext = ${lines(b, 10)}\n`,
  toml: (t, b) => `# ${t}\n[notes]\ntext = """\n${lines(b, 10)}\n"""\n`,
  // One quoted, comma-terminated word per token; the last element closes the array on its own line.
  json: (t, b) => `{\n "title": "${t}",\n "body": [\n${b.map((w, i) => `  "${esc(w)}"${i === b.length - 1 ? '' : ','}`).join('\n')}\n ]\n}\n`,
  // Flow sequence, so the dash of a block sequence does not become a token per line.
  yaml: (t, b) => `title: ${t}\nbody: [\n${b.map((w, i) => ` ${w}${i === b.length - 1 ? '' : ','}`).join('\n')}\n]\n`,
  xml: (t, b) => `<?xml version="1.0" encoding="utf-8"?>\n<note>\n <title>${t}</title>\n <body>\n${lines(b, 10)}\n </body>\n</note>\n`,
  csv: (t, b) => `index,body\n${b.map((w, i) => `${i},${w}`).join('\n')}\n`,
  // One comment block, not a marker per line.
  code_hash: (t, b) => `# ${t}\n"""\n${lines(b, 10)}\n"""\n`,
  shell: (t, b) => `# ${t}\n: <<'NOTES'\n${lines(b, 10)}\nNOTES\n`,
  powershell: (t, b) => `# ${t}\n<#\n${lines(b, 10)}\n#>\n`,
  code_slash: (t, b) => `// ${t}\n/*\n${lines(b, 10)}\n*/\n`,
  sql: (t, b) => `-- ${t}\n/*\n${lines(b, 10)}\n*/\n`,
  html: (t, b) => `<!doctype html>\n<html>\n<head><title>${t}</title></head>\n<body>\n<p>\n${lines(b, 10)}\n</p>\n</body>\n</html>\n`,
  css: (t, b) => `/* ${t} */\n/*\n${lines(b, 10)}\n*/\nbody{margin:0;}\n`,
  msbuild: (t, b) => `<Project>\n <!-- ${t} -->\n <PropertyGroup>\n  <Notes>\n${lines(b, 10)}\n  </Notes>\n </PropertyGroup>\n</Project>\n`,
  sln: (t, b) => `Microsoft Visual Studio Solution File, Format Version 12.00\n# ${t}\n${lines(b, 10)}\n`,
};

function esc(w) { return w.replace(/\\/g, '\\\\').replace(/"/g, '\\"'); }

function lines(words, per) {
  const out = [];
  for (let i = 0; i < words.length; i += per) out.push(words.slice(i, i + per).join(' '));
  return out.join('\n');
}

const formats = [
  ['.txt', 'plain', 'docs'], ['.md', 'md', 'docs'], ['.markdown', 'md', 'docs'],
  ['.log', 'log', 'docs'], ['.ini', 'ini', 'docs'], ['.toml', 'toml', 'docs'],
  ['.json', 'json', 'data'], ['.yml', 'yaml', 'data'], ['.yaml', 'yaml', 'data'],
  ['.xml', 'xml', 'data'], ['.csv', 'csv', 'data'],
  ['.cs', 'code_slash', 'code'], ['.js', 'code_slash', 'code'], ['.ts', 'code_slash', 'code'],
  ['.jsx', 'code_slash', 'code'], ['.tsx', 'code_slash', 'code'], ['.java', 'code_slash', 'code'],
  ['.go', 'code_slash', 'code'], ['.rs', 'code_slash', 'code'], ['.scss', 'code_slash', 'code'],
  ['.py', 'code_hash', 'code'], ['.ps1', 'powershell', 'code'], ['.sh', 'shell', 'code'],
  ['.sql', 'sql', 'code'],
  ['.html', 'html', 'web'], ['.css', 'css', 'web'],
  ['.csproj', 'msbuild', 'build'], ['.props', 'msbuild', 'build'], ['.targets', 'msbuild', 'build'],
  ['.sln', 'sln', 'build'], ['.slnx', 'msbuild', 'build'],
];

// ── Generation ───────────────────────────────────────────────────────────────────────────────
function tokenCount(text) { const m = text.match(/\S+/g); return m ? m.length : 0; }

function body(topic, lang, count) {
  // Topic sentences first, then neutral filler, to exactly `count` whitespace tokens.
  const words = [];
  const src = topic[lang].lines;
  let i = 0;
  while (words.length < count) {
    const sentence = src[i % src.length].split(/\s+/);
    for (const w of sentence) { if (words.length < count) words.push(w); }
    i++;
    if (i > src.length * 3) break; // sentences exhausted; fill the rest with neutral words
  }
  const pad = filler[lang];
  let j = 0;
  while (words.length < count) { words.push(pad[j % pad.length]); j++; }
  return words.slice(0, count);
}

function build(topic, lang, ext, kind, target) {
  const title = topic[lang].title;
  const fn = wrap[kind];
  // Measure the wrapper's own tokens with a single-word body, then size the body to hit `target`.
  const overhead = tokenCount(fn(title, ['x'])) - 1;
  let n = Math.max(1, target - overhead);
  let text = fn(title, body(topic, lang, n));
  let actual = tokenCount(text);

  // The wrappers are token-stable by construction, so this converges in one step. It is a loop, and
  // it throws rather than returning a near miss, because the whole point of the sizes is that a
  // file lands exactly on a chunk boundary or exactly short of one — a corpus that is approximately
  // the designed shape would test nothing in particular.
  for (let i = 0; i < 4 && actual !== target; i++) {
    n = Math.max(1, n + (target - actual));
    text = fn(title, body(topic, lang, n));
    actual = tokenCount(text);
  }

  if (actual !== target) {
    throw new Error(`${kind}${ext}: wanted ${target} tokens, produced ${actual} — the wrapper's overhead is not constant`);
  }

  return { text, actual };
}

const manifest = [];
let written = 0;

function write(rel, text, opts = {}) {
  const full = path.join(OUT, rel);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  let out = text;
  if (opts.crlf) out = out.replace(/\n/g, '\r\n');
  const buf = opts.bom ? Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(out, 'utf8')]) : Buffer.from(out, 'utf8');
  fs.writeFileSync(full, buf);
  written++;
  return full;
}

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

// Every extension × every language, cycling through exact and trailing-partial sizes so both
// shapes appear in every format and every language.
const sizes = [];
for (let i = 0; i < Math.max(exactSizes.length, partialSizes.length) * 2; i++) {
  const e = exactSizes[i % exactSizes.length], p = partialSizes[i % partialSizes.length];
  sizes.push(i % 2 === 0 ? e : p);
}

let s = 0;
formats.forEach(([ext, kind, dir], fi) => {
  langs.forEach((lang, li) => {
    // The topic follows the format, not the (format, language) pair. Mixing the language into the
    // index gave each topic exactly one language among the format files — beekeeping only ever in
    // English — so a Russian question about it had one in-language document in the whole corpus and
    // its nearest competitor was the English copy. That measures the labels, not the embedder.
    const topic = topics[fi % topics.length];
    const target = sizes[s++ % sizes.length];
    const { text, actual } = build(topic, lang, ext, kind, target);
    const name = `${topic.id}-${lang}-${actual}t${ext}`;
    const rel = path.join(dir, name);
    write(rel, text);
    manifest.push({ file: name, rel, topic: topic.id, lang, ext, tokens: actual,
      chunks: chunkCount(actual), lastChunk: lastChunkTokens(actual),
      exact: (actual - CHUNK) % STEP === 0 && actual >= CHUNK });
  });
});

// A second pass over .md and .txt so every topic has a full-length document in all three
// languages — these are what the labelled queries are scored against.
topics.forEach((topic, ti) => {
  langs.forEach((lang, li) => {
    const target = ti % 2 === 0 ? exactSizes[(ti + li) % exactSizes.length] : partialSizes[(ti + li) % partialSizes.length];
    const ext = li === 0 ? '.md' : li === 1 ? '.txt' : '.markdown';
    const { text, actual } = build(topic, lang, ext, ext === '.txt' ? 'plain' : 'md', target);
    const name = `${topic.id}-${lang}-main-${actual}t${ext}`;
    const rel = path.join('library', lang, name);
    write(rel, text);
    manifest.push({ file: name, rel, topic: topic.id, lang, ext, tokens: actual,
      chunks: chunkCount(actual), lastChunk: lastChunkTokens(actual),
      exact: (actual - CHUNK) % STEP === 0 && actual >= CHUNK, main: true });
  });
});

// ── Edges ────────────────────────────────────────────────────────────────────────────────────
// Each of these exists because some part of the pipeline has a documented behaviour for it.
// These exist to exercise the reader, not retrieval, so they carry **no topic text**: filler only.
// Built from a topic, they are genuine documents about it — and being unlabelled, a correct hit on
// one scores as a miss. Two of the six misses in the first clean run were exactly that.
const edge = { en: { title: 'Edge case fixture', lines: ['This fixture carries no subject matter at all.'] } };
const neutral = (target, kind = 'plain', ext = '.txt') => build(edge, 'en', ext, kind, target).text;

write('edge/empty.txt', '');
write('edge/whitespace-only.md', '\n\n   \t\n');
write('edge/one-token.txt', 'token\n');
write('edge/exactly-one-chunk-256t.txt', neutral(256));
write('edge/one-over-a-chunk-257t.txt', neutral(257));
write('edge/crlf-line-endings.md', neutral(480, 'md', '.md'), { crlf: true });
write('edge/utf8-bom.md', neutral(480, 'md', '.md'), { bom: true });
write('edge/bom-and-crlf.txt', neutral(300), { bom: true, crlf: true });

// Over the 1 MB default bound: indexed folders skip it, and the log says so.
write('edge/over-size-limit.txt', neutral(5000) + 'padding '.repeat(140000));

// Extensions the registry does not claim: they must be ignored, not indexed as text.
write('edge/not-indexed.pdf', 'this file is not plain text as far as the registry is concerned\n');
write('edge/not-indexed.docx', 'nor is this one\n');
write('edge/not-indexed.bin', '\x00\x01\x02binary\x00\n');

// Directories the scanner skips by name — a file here must never appear in a search result.
write('node_modules/ignored-package/index.js', '// must not be indexed\nconst ignored = true;\n');
write('.git/config-like.txt', 'must not be indexed\n');
write('build-output/bin/artifact.txt', 'must not be indexed if bin is skipped\n');
write('build-output/obj/artifact.txt', 'must not be indexed if obj is skipped\n');

// Nesting, to give the walk some depth.
const deep = build(topics[6], 'en', '.md', 'md', 704);
write(path.join('library', 'deep', 'a', 'b', 'c', `nested-${deep.actual}t.md`), deep.text);
manifest.push({ file: `nested-${deep.actual}t.md`, rel: 'library/deep/a/b/c', topic: topics[6].id, lang: 'en',
  ext: '.md', tokens: deep.actual, chunks: chunkCount(deep.actual), lastChunk: lastChunkTokens(deep.actual), exact: true });

// ── Labelled queries ─────────────────────────────────────────────────────────────────────────
// Same format as the repository's own BenchmarkCorpus.queries.tsv: query<TAB>relevant files.
const rows = ['# Multilingual semantic-search ground truth for the generated corpus.',
  '# Format: query<TAB>relevant file(s), comma separated. Lines starting with # are ignored.',
  '# A query is worded to avoid the distinctive words of the file it should find, so ranking it',
  '# correctly takes meaning rather than shared vocabulary. The "cross" queries are asked in a',
  '# language other than the document\'s, which only a multilingual embedder can answer.'];

// Relevance is "a file about this topic", not "the file I happened to call the main one". Every
// format file carries its topic's sentences too, so labelling only the library copies scored a
// correct hit on beekeeping-en-256t.ini as a miss and pinned Recall@1 at zero whatever the embedder
// did. What a question deserves is a file that answers it; which of the copies it found is not the
// embedder's business.
for (const topic of topics) {
  for (const lang of langs) {
    const files = manifest.filter(m => m.topic === topic.id && m.lang === lang).map(m => m.file);
    if (files.length === 0) continue;
    rows.push(`${topic.queries[lang]}\t${files.join(',')}`);
  }
  // Cross-lingual: one question whose answer sits in every language's copies.
  const all = manifest.filter(m => m.topic === topic.id).map(m => m.file);
  rows.push(`${topic.queries.cross}\t${all.join(',')}`);
}
fs.writeFileSync(path.join(OUT, 'queries.tsv'), rows.join('\n') + '\n');

// ── Report ───────────────────────────────────────────────────────────────────────────────────
const indexed = manifest.length;
const totalTokens = manifest.reduce((a, m) => a + m.tokens, 0);
const totalChunks = manifest.reduce((a, m) => a + m.chunks, 0);
const exact = manifest.filter(m => m.exact).length;

// `.meta`, not `.json`: this file lives inside the folder being indexed, and the registry claims
// `.json`. Indexed, it becomes a document naming every topic in the corpus — which is exactly the
// kind of thing a semantic search ranks highly, so the instrument's own bookkeeping would compete
// with the documents it is scoring. The same reason the generator is never copied in here.
fs.writeFileSync(path.join(OUT, 'corpus-manifest.meta'), JSON.stringify({
  generated: new Date().toISOString(), chunkSizeTokens: CHUNK, chunkStepTokens: STEP,
  files: manifest.length, totalTokens, totalChunks, exactChunkFiles: exact,
  partialChunkFiles: manifest.length - exact, entries: manifest,
}, null, 1));

console.log(`files written      : ${written}`);
console.log(`labelled documents : ${indexed} (${exact} end on a full chunk, ${indexed - exact} leave a short one)`);
console.log(`tokens             : ${totalTokens}`);
console.log(`chunks (predicted) : ${totalChunks}`);
console.log(`languages          : ${langs.join(', ')}`);
console.log(`extensions         : ${new Set(manifest.map(m => m.ext)).size}`);
console.log(`queries            : ${rows.filter(r => !r.startsWith('#')).length}`);
