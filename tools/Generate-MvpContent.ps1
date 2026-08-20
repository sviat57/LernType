param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$appRoot = Join-Path $ProjectRoot 'src\WortBruecke.App'
$catalogPath = Join-Path $appRoot 'Content\catalog.json'
$imageRoot = Join-Path $appRoot 'Assets\Images'
New-Item -ItemType Directory -Path (Split-Path $catalogPath) -Force | Out-Null
New-Item -ItemType Directory -Path $imageRoot -Force | Out-Null

$themes = @(
    [ordered]@{ Id=1; Key='essen'; Icon='food'; Ru='Еда'; De='Essen'; Color='#C95D45'; Words=@(
        'яблоко|der Apfel|noun|A0|🍎','хлеб|das Brot|noun|A0|🍞','вода|das Wasser|noun|A1|💧','сыр|der Käse|noun|A1|🧀','молоко|die Milch|noun|A1|🥛',
        'кофе|der Kaffee|noun|A1|☕','чай|der Tee|noun|A1|♨','овощ|das Gemüse|noun|A2|🥦','фрукт|das Obst|noun|A2|🍐','суп|die Suppe|noun|A1|🍲',
        'рыба|der Fisch|noun|A1|🐟','мясо|das Fleisch|noun|A1|🍖','завтрак|das Frühstück|noun|A2|🥣','есть|essen|verb|A1|🍴','готовить|kochen|verb|A2|🥘') },
    [ordered]@{ Id=2; Key='familie'; Icon='people'; Ru='Семья'; De='Familie'; Color='#B76B86'; Words=@(
        'мать|die Mutter|noun|A0|♀','отец|der Vater|noun|A0|♂','родители|die Eltern|noun|A2|⚭','сестра|die Schwester|noun|A1|♙','брат|der Bruder|noun|A1|♟',
        'ребёнок|das Kind|noun|A1|★','дочь|die Tochter|noun|A2|♡','сын|der Sohn|noun|A2|♢','бабушка|die Großmutter|noun|A2|♕','дедушка|der Großvater|noun|A2|♔',
        'тётя|die Tante|noun|A2|♧','дядя|der Onkel|noun|A2|♣','двоюродный брат|der Cousin|noun|A2|◇','женатый|verheiratet|adjective|A2|💍','вместе|zusammen|adverb|A1|🤝') },
    [ordered]@{ Id=3; Key='reisen'; Icon='travel'; Ru='Путешествия'; De='Reisen'; Color='#4E8298'; Words=@(
        'путешествие|die Reise|noun|A0|🧳','поезд|der Zug|noun|A0|🚆','самолёт|das Flugzeug|noun|A1|✈','вокзал|der Bahnhof|noun|A2|🚉','билет|die Fahrkarte|noun|A1|🎫',
        'отель|das Hotel|noun|A1|🏨','чемодан|der Koffer|noun|A1|▣','паспорт|der Reisepass|noun|A2|▤','карта|die Landkarte|noun|A2|⌖','улица|die Straße|noun|A1|↟',
        'машина|das Auto|noun|A1|🚗','корабль|das Schiff|noun|A2|⛴','гора|der Berg|noun|A1|▲','море|das Meer|noun|A1|≋','путешествовать|reisen|verb|A2|⌁') },
    [ordered]@{ Id=4; Key='arbeit'; Icon='work'; Ru='Работа'; De='Arbeit'; Color='#7C6A58'; Words=@(
        'работа|die Arbeit|noun|A0|💼','профессия|der Beruf|noun|A2|▧','офис|das Büro|noun|A0|🏢','коллега|der Kollege|noun|A2|♟','начальник|der Chef|noun|A2|♜',
        'компьютер|der Computer|noun|A1|💻','совещание|die Besprechung|noun|B1|◎','электронная почта|die E-Mail|noun|A2|✉','задача|die Aufgabe|noun|A2|✓','перерыв|die Pause|noun|A1|Ⅱ',
        'зарплата|das Gehalt|noun|B1|€','договор|der Vertrag|noun|B1|▤','фирма|die Firma|noun|A2|🏭','работать|arbeiten|verb|A1|⚒','учиться|lernen|verb|A1|📚') },
    [ordered]@{ Id=5; Key='alltag'; Icon='daily'; Ru='Быт'; De='Alltag'; Color='#D08A43'; Words=@(
        'утро|der Morgen|noun|A0|☀','вечер|der Abend|noun|A0|◐','день|der Tag|noun|A1|☼','неделя|die Woche|noun|A1|▦','покупка|der Einkauf|noun|A2|🛒',
        'убирать|putzen|verb|A2|⌁','мыть|waschen|verb|A2|≈','просыпаться|aufwachen|verb|A2|⏰','спать|schlafen|verb|A1|☾','идти|gehen|verb|A1|➜',
        'ехать|fahren|verb|A1|➤','открывать|öffnen|verb|A1|□','закрывать|schließen|verb|A1|▣','ждать|warten|verb|A2|⌛','торопиться|sich beeilen|verb|B1|⚡') },
    [ordered]@{ Id=6; Key='natur'; Icon='nature'; Ru='Природа'; De='Natur'; Color='#4F8064'; Words=@(
        'дерево|der Baum|noun|A0|♧','цветок|die Blume|noun|A0|✿','лес|der Wald|noun|A1|♠','река|der Fluss|noun|A2|≈','озеро|der See|noun|A2|◉',
        'дождь|der Regen|noun|A1|☂','снег|der Schnee|noun|A1|❄','солнце|die Sonne|noun|A1|☀','луна|der Mond|noun|A1|☾','небо|der Himmel|noun|A1|☁',
        'животное|das Tier|noun|A1|🐾','птица|der Vogel|noun|A1|⌁','ветер|der Wind|noun|A2|≋','земля|die Erde|noun|A1|⊕','зелёный|grün|adjective|A1|🌿') },
    [ordered]@{ Id=7; Key='zahlen-zeit'; Icon='time'; Ru='Числа и время'; De='Zahlen und Zeit'; Color='#6D6CA1'; Words=@(
        'один|eins|number|A0|1','два|zwei|number|A0|2','три|drei|number|A1|3','десять|zehn|number|A1|10','сто|hundert|number|A1|100',
        'час|die Stunde|noun|A1|🕐','минута|die Minute|noun|A1|⏱','сегодня|heute|adverb|A1|●','завтра|morgen|adverb|A1|→','вчера|gestern|adverb|A1|←',
        'рано|früh|adverb|A2|☀','поздно|spät|adverb|A2|☾','понедельник|der Montag|noun|A1|M','выходные|das Wochenende|noun|A1|▦','время|die Zeit|noun|A1|⌚') },
    [ordered]@{ Id=8; Key='koerper-gesundheit'; Icon='health'; Ru='Тело и здоровье'; De='Körper und Gesundheit'; Color='#B95757'; Words=@(
        'голова|der Kopf|noun|A0|◯','рука|die Hand|noun|A0|✋','глаз|das Auge|noun|A1|◉','сердце|das Herz|noun|A1|♥','спина|der Rücken|noun|A2|↥',
        'живот|der Bauch|noun|A2|◒','врач|der Arzt|noun|A1|⚕','больница|das Krankenhaus|noun|A2|✚','лекарство|das Medikament|noun|A2|💊','боль|der Schmerz|noun|A2|⚡',
        'здоровый|gesund|adjective|A1|◆','больной|krank|adjective|A1|◇','сон|der Schlaf|noun|A2|Z','дышать|atmen|verb|A2|≋','помощь|die Hilfe|noun|A1|SOS') },
    [ordered]@{ Id=9; Key='wohnen'; Icon='home'; Ru='Жильё'; De='Wohnen'; Color='#4F7791'; Words=@(
        'дом|das Haus|noun|A0|⌂','квартира|die Wohnung|noun|A0|▥','комната|das Zimmer|noun|A1|□','кухня|die Küche|noun|A1|♨','ванная|das Badezimmer|noun|A2|≈',
        'спальня|das Schlafzimmer|noun|A2|▱','стол|der Tisch|noun|A1|▰','стул|der Stuhl|noun|A1|▥','окно|das Fenster|noun|A1|▦','дверь|die Tür|noun|A1|▯',
        'ключ|der Schlüssel|noun|A1|🔑','сад|der Garten|noun|A1|✿','аренда|die Miete|noun|B1|€','переезжать|umziehen|verb|A2|📦','жить|wohnen|verb|A1|⌂') },
    [ordered]@{ Id=10; Key='freizeit'; Icon='leisure'; Ru='Досуг'; De='Freizeit'; Color='#96688E'; Words=@(
        'книга|das Buch|noun|A0|📖','музыка|die Musik|noun|A0|♪','фильм|der Film|noun|A1|▻','спорт|der Sport|noun|A1|⚽','игра|das Spiel|noun|A1|🎲',
        'друг|der Freund|noun|A1|♟','танцевать|tanzen|verb|A1|♫','читать|lesen|verb|A1|▤','плавать|schwimmen|verb|A2|≈','ходить в поход|wandern|verb|A2|▲',
        'фотография|das Foto|noun|A1|📷','театр|das Theater|noun|A2|◒','концерт|das Konzert|noun|A2|♬','велосипед|das Fahrrad|noun|A1|🚲','выходные|das Wochenende|noun|A1|▦') }
)

$catalog = [ordered]@{ revision=4; themes=@(); words=@(); sentences=@(); passages=@(); grammarTasks=@() }
foreach ($theme in $themes) {
    $catalog.themes += [ordered]@{ id=$theme.Id; key=$theme.Key; iconKey=$theme.Icon; names=[ordered]@{ 'ru-RU'=$theme.Ru; 'de-DE'=$theme.De } }
    $index = 0
    foreach ($spec in $theme.Words) {
        $index++
        $parts = $spec -split '\|', 5
        $id = ($theme.Id * 100) + $index
        $catalog.words += [ordered]@{
            id=$id; themeId=$theme.Id; imagePath="Assets/Images/$($theme.Key)/$id.png"; level=$parts[3]; partOfSpeech=$parts[2]
            translations=[ordered]@{ 'ru-RU'=$parts[0]; 'de-DE'=$parts[1] }; examples=[ordered]@{}
        }

        $themeDir = Join-Path $imageRoot $theme.Key
        New-Item -ItemType Directory -Path $themeDir -Force | Out-Null
        Add-Type -AssemblyName System.Drawing
        $bitmap = [System.Drawing.Bitmap]::new(360, 360)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#FAF8F3'))
        $accent = [System.Drawing.ColorTranslator]::FromHtml($theme.Color)
        $soft = [System.Drawing.Color]::FromArgb(38, $accent.R, $accent.G, $accent.B)
        $graphics.FillEllipse([System.Drawing.SolidBrush]::new($soft), 40, 40, 280, 280)
        $graphics.DrawEllipse([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(80, $accent.R, $accent.G, $accent.B), 3), 40, 40, 280, 280)
        $fontSize = if ($parts[4].Length -gt 3) { 80 } elseif ($parts[4].Length -gt 1) { 105 } else { 132 }
        $font = [System.Drawing.Font]::new('Segoe UI Emoji', $fontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $graphics.DrawString($parts[4], $font, [System.Drawing.SolidBrush]::new($accent), [System.Drawing.RectangleF]::new(32, 28, 296, 304), $format)
        $bitmap.Save((Join-Path $themeDir "$id.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $format.Dispose(); $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

$sentenceSpecs = @(
    '1101|1|A1|Я ем яблоко.|Ich esse einen Apfel.',
    '1102|1|A2|За завтраком мы пьём кофе и едим хлеб с сыром.|Zum Frühstück trinken wir Kaffee und essen Brot mit Käse.',
    '1103|1|B1|Когда у меня мало времени, я готовлю простой овощной суп.|Wenn ich wenig Zeit habe, koche ich eine einfache Gemüsesuppe.',
    '1201|2|A1|Моя сестра живёт вместе с родителями.|Meine Schwester wohnt bei unseren Eltern.',
    '1202|2|A2|По выходным бабушка навещает своих внуков.|Am Wochenende besucht die Großmutter ihre Enkelkinder.',
    '1203|2|B1|Хотя мой брат живёт далеко, мы часто разговариваем друг с другом.|Obwohl mein Bruder weit weg wohnt, sprechen wir oft miteinander.',
    '1301|3|A1|Поезд прибывает на вокзал в девять часов.|Der Zug kommt um neun Uhr am Bahnhof an.',
    '1302|3|A2|Перед поездкой я кладу паспорт и билет в чемодан.|Vor der Reise lege ich den Reisepass und die Fahrkarte in den Koffer.',
    '1303|3|B1|Если погода останется хорошей, завтра мы поплывём на корабле.|Wenn das Wetter gut bleibt, fahren wir morgen mit dem Schiff.',
    '1401|4|A1|Я работаю в небольшом офисе.|Ich arbeite in einem kleinen Büro.',
    '1402|4|A2|После совещания коллега отправляет мне электронное письмо.|Nach der Besprechung schickt mir der Kollege eine E-Mail.',
    '1403|4|B1|Прежде чем подписать договор, она внимательно проверила все условия.|Bevor sie den Vertrag unterschrieb, prüfte sie alle Bedingungen sorgfältig.',
    '1501|5|A1|Каждое утро я открываю окно.|Jeden Morgen öffne ich das Fenster.',
    '1502|5|A2|После работы он делает покупки и убирает квартиру.|Nach der Arbeit kauft er ein und putzt die Wohnung.',
    '1503|5|B1|Мне пришлось поторопиться, потому что автобус уже ждал на остановке.|Ich musste mich beeilen, weil der Bus schon an der Haltestelle wartete.',
    '1601|6|A1|Солнце светит над зелёным лесом.|Die Sonne scheint über dem grünen Wald.',
    '1602|6|A2|После дождя мы гуляли вдоль реки.|Nach dem Regen gingen wir am Fluss entlang spazieren.',
    '1603|6|B1|Когда поднялся сильный ветер, птицы спрятались среди деревьев.|Als ein starker Wind aufkam, versteckten sich die Vögel zwischen den Bäumen.',
    '1701|7|A1|Сегодня понедельник, и у меня есть час.|Heute ist Montag, und ich habe eine Stunde Zeit.',
    '1702|7|A2|Вчера мы пришли поздно, а завтра встанем рано.|Gestern kamen wir spät, aber morgen stehen wir früh auf.',
    '1703|7|B1|К тому времени, когда встреча закончилась, прошло почти сто минут.|Bis die Besprechung endete, waren fast hundert Minuten vergangen.',
    '1801|8|A1|У меня болит голова.|Ich habe Kopfschmerzen.',
    '1802|8|A2|Врач сказал, что мне нужно больше спать.|Der Arzt sagte, dass ich mehr schlafen müsse.',
    '1803|8|B1|Несмотря на лекарство, боль в спине не стала слабее.|Trotz des Medikaments wurden die Rückenschmerzen nicht schwächer.',
    '1901|9|A1|В моей комнате есть стол и стул.|In meinem Zimmer stehen ein Tisch und ein Stuhl.',
    '1902|9|A2|Мы ищем квартиру с большой кухней и садом.|Wir suchen eine Wohnung mit einer großen Küche und einem Garten.',
    '1903|9|B1|После того как хозяин повысил аренду, семья решила переехать.|Nachdem der Vermieter die Miete erhöht hatte, beschloss die Familie umzuziehen.',
    '2001|10|A1|Вечером я читаю книгу.|Am Abend lese ich ein Buch.',
    '2002|10|A2|В выходные друзья ходят в театр или на концерт.|Am Wochenende gehen die Freunde ins Theater oder zu einem Konzert.',
    '2003|10|B1|Если бы погода была теплее, мы бы отправились в поход на велосипедах.|Wenn das Wetter wärmer wäre, würden wir eine Fahrradtour machen.',
    '1100|1|A0|Я пью воду.|Ich trinke Wasser.',
    '1104|1|B2|Хотя рецепт казался простым, вкус блюда зависел от качества каждого продукта.|Obwohl das Rezept einfach wirkte, hing der Geschmack des Gerichts von der Qualität jeder Zutat ab.',
    '1105|1|C1|То, как общество говорит о еде, отражает не только привычки, но и представления о принадлежности.|Die Art, wie eine Gesellschaft über Essen spricht, spiegelt nicht nur Gewohnheiten, sondern auch Vorstellungen von Zugehörigkeit wider.',
    '1106|1|C2|Изысканность блюда проявилась не в редкости ингредиентов, а в едва уловимом равновесии их ароматов.|Die Raffinesse des Gerichts zeigte sich nicht in der Seltenheit seiner Zutaten, sondern im kaum wahrnehmbaren Gleichgewicht ihrer Aromen.',
    '1200|2|A0|Мою маму зовут Анна.|Meine Mutter heißt Anna.',
    '1204|2|B2|Даже когда поколения оценивают события по-разному, общие воспоминания помогают им понимать друг друга.|Selbst wenn die Generationen Ereignisse unterschiedlich bewerten, helfen gemeinsame Erinnerungen ihnen, einander zu verstehen.',
    '1205|2|C1|Семейные роли меняются по мере того, как жизненные планы отдельных людей вступают в противоречие с традиционными ожиданиями.|Familiäre Rollen verändern sich, sobald die Lebensentwürfe Einzelner mit traditionellen Erwartungen in Konflikt geraten.',
    '1206|2|C2|Близость, которую семья считает само собой разумеющейся, нередко требует особенно точного разграничения заботы и вмешательства.|Die Nähe, die eine Familie für selbstverständlich hält, verlangt nicht selten eine besonders präzise Abgrenzung zwischen Fürsorge und Einmischung.',
    '1300|3|A0|Это мой билет.|Das ist meine Fahrkarte.',
    '1304|3|B2|Поскольку рейс отменили без предупреждения, пассажиры потребовали понятного объяснения и подходящей замены.|Da der Flug ohne Vorwarnung gestrichen worden war, verlangten die Reisenden eine nachvollziehbare Erklärung und eine angemessene Alternative.',
    '1305|3|C1|Путешествие меняет взгляд на привычное, если человек не просто потребляет впечатления, а сравнивает разные способы жизни.|Reisen verändert den Blick auf das Vertraute, sofern man Eindrücke nicht bloß konsumiert, sondern unterschiedliche Lebensweisen miteinander vergleicht.',
    '1306|3|C2|Чужое место перестаёт быть декорацией лишь тогда, когда путешественник допускает, что его собственная перспектива ограниченна.|Ein fremder Ort hört erst dann auf, bloße Kulisse zu sein, wenn der Reisende zulässt, dass die eigene Perspektive begrenzt ist.',
    '1400|4|A0|Я работаю здесь.|Ich arbeite hier.',
    '1404|4|B2|Удалённая работа даёт больше свободы, однако требует ясных договорённостей о доступности и ответственности.|Fernarbeit schafft mehr Freiheit, erfordert jedoch klare Absprachen über Erreichbarkeit und Verantwortung.',
    '1405|4|C1|Организация остаётся способной к развитию лишь тогда, когда критические замечания воспринимаются как источник знаний, а не как угроза.|Eine Organisation bleibt nur dann entwicklungsfähig, wenn kritische Rückmeldungen als Wissensquelle und nicht als Bedrohung verstanden werden.',
    '1406|4|C2|Профессиональная компетентность проявляется не только в уверенных решениях, но и в способности точно обозначить границы собственного знания.|Berufliche Kompetenz zeigt sich nicht nur in sicheren Entscheidungen, sondern auch in der Fähigkeit, die Grenzen des eigenen Wissens präzise zu benennen.',
    '1500|5|A0|Сегодня хороший день.|Heute ist ein guter Tag.',
    '1504|5|B2|Чтобы повседневные обязанности не отнимали всё внимание, я заранее определяю, какие дела действительно важны.|Damit alltägliche Pflichten nicht meine ganze Aufmerksamkeit beanspruchen, lege ich vorher fest, welche Aufgaben tatsächlich wichtig sind.',
    '1505|5|C1|Привычки облегчают жизнь, но могут мешать изменениям, если их полезность больше никогда не подвергается сомнению.|Gewohnheiten erleichtern das Leben, können Veränderungen jedoch behindern, wenn ihr Nutzen nicht mehr hinterfragt wird.',
    '1506|5|C2|Повседневность кажется незаметной именно потому, что её повторяющиеся структуры формируют наше восприятие раньше, чем мы успеваем их осмыслить.|Der Alltag erscheint gerade deshalb unscheinbar, weil seine wiederkehrenden Strukturen unsere Wahrnehmung prägen, bevor wir sie reflektieren können.',
    '1600|6|A0|Дерево зелёное.|Der Baum ist grün.',
    '1604|6|B2|Даже небольшие природные территории в городе улучшают климат и дают животным необходимое убежище.|Selbst kleine Naturräume in der Stadt verbessern das Klima und bieten Tieren einen notwendigen Rückzugsort.',
    '1605|6|C1|Экологические меры принимаются охотнее, когда их долгосрочная польза объясняется так же конкретно, как и непосредственные расходы.|Ökologische Maßnahmen werden eher akzeptiert, wenn ihr langfristiger Nutzen ebenso konkret erläutert wird wie die unmittelbaren Kosten.',
    '1606|6|C2|Представление о нетронутой природе скрывает тот факт, что даже кажущиеся дикими ландшафты часто отмечены человеческими решениями.|Die Vorstellung einer unberührten Natur verdeckt, dass selbst vermeintlich wilde Landschaften häufig von menschlichen Entscheidungen geprägt sind.',
    '1700|7|A0|Сейчас два часа.|Es ist zwei Uhr.',
    '1704|7|B2|Хотя срок был известен давно, команда недооценила время, необходимое для последней проверки.|Obwohl der Termin lange bekannt war, unterschätzte das Team die Zeit, die für die abschließende Prüfung nötig war.',
    '1705|7|C1|Ощущение нехватки времени возникает не только из-за количества задач, но и из-за постоянного переключения внимания.|Das Gefühl des Zeitmangels entsteht nicht nur durch die Menge der Aufgaben, sondern auch durch den ständigen Wechsel der Aufmerksamkeit.',
    '1706|7|C2|Время воспринимается как дефицитный ресурс, хотя его ценность всякий раз определяется тем, чему мы сознательно позволяем длиться.|Zeit wird als knappe Ressource wahrgenommen, obwohl ihr Wert jedes Mal dadurch bestimmt wird, was wir bewusst dauern lassen.',
    '1800|8|A0|У меня болит рука.|Meine Hand tut weh.',
    '1804|8|B2|Если симптомы сохраняются несколько дней, их следует обследовать, даже если они поначалу кажутся безобидными.|Wenn die Beschwerden mehrere Tage anhalten, sollten sie untersucht werden, auch wenn sie zunächst harmlos erscheinen.',
    '1805|8|C1|Профилактика эффективна прежде всего тогда, когда медицинская информация понятна и учитывает реальные условия жизни людей.|Prävention ist vor allem dann wirksam, wenn medizinische Informationen verständlich sind und die tatsächlichen Lebensbedingungen der Menschen berücksichtigen.',
    '1806|8|C2|Здоровье нельзя свести к отсутствию измеримых нарушений, поскольку субъективное благополучие также зависит от социальных и психологических условий.|Gesundheit lässt sich nicht auf das Fehlen messbarer Störungen reduzieren, da subjektives Wohlbefinden ebenso von sozialen und psychischen Bedingungen abhängt.',
    '1900|9|A0|Это мой дом.|Das ist mein Haus.',
    '1904|9|B2|Многие выбирают меньшую квартиру, если она хорошо связана с транспортом и снижает ежедневные расходы.|Viele entscheiden sich für eine kleinere Wohnung, wenn sie gut an den Verkehr angebunden ist und die täglichen Kosten senkt.',
    '1905|9|C1|Жилищная политика определяет не только цены, но и то, какие социальные группы могут оставаться частью городского сообщества.|Wohnungspolitik bestimmt nicht nur die Preise, sondern auch, welche sozialen Gruppen Teil der städtischen Gemeinschaft bleiben können.',
    '1906|9|C2|Дом становится местом принадлежности не благодаря неизменности, а благодаря следам перемен, которые его обитатели в нём оставляют.|Ein Zuhause wird nicht durch Unveränderlichkeit zum Ort der Zugehörigkeit, sondern durch die Spuren des Wandels, die seine Bewohner darin hinterlassen.',
    '2000|10|A0|Я читаю книгу.|Ich lese ein Buch.',
    '2004|10|B2|Свободное время восстанавливает силы только тогда, когда оно не превращается в ещё один список обязательных достижений.|Freizeit wirkt nur dann erholsam, wenn sie nicht zu einer weiteren Liste verpflichtender Leistungen wird.',
    '2005|10|C1|Культурные увлечения открывают новые перспективы, поскольку требуют временно принять правила и образы чужого опыта.|Kulturelle Interessen eröffnen neue Perspektiven, weil sie verlangen, sich vorübergehend auf die Regeln und Bilder fremder Erfahrungen einzulassen.',
    '2006|10|C2|Подлинная игра сохраняет ценность именно потому, что её смысл не исчерпывается измеримым результатом.|Echtes Spiel bewahrt seinen Wert gerade deshalb, weil sein Sinn sich nicht in einem messbaren Ergebnis erschöpft.'
)
foreach ($spec in $sentenceSpecs) {
    $parts = $spec -split '\|', 5
    $catalog.sentences += [ordered]@{
        id=[int]$parts[0]; themeId=[int]$parts[1]; level=$parts[2]
        translations=[ordered]@{ 'ru-RU'=$parts[3]; 'de-DE'=$parts[4] }
    }
}

$catalog.passages = @(
    [ordered]@{ id=5; key='first_morning'; titles=[ordered]@{'ru-RU'='Первое утро';'de-DE'='Der erste Morgen'}; kind='Everyday'; level='A0'; topic='foundation'; segments=@(
        [ordered]@{id=5001;order=1;translations=[ordered]@{'ru-RU'='Меня зовут Лена.';'de-DE'='Ich heiße Lena.'}},
        [ordered]@{id=5002;order=2;translations=[ordered]@{'ru-RU'='Я живу в Берлине.';'de-DE'='Ich wohne in Berlin.'}},
        [ordered]@{id=5003;order=3;translations=[ordered]@{'ru-RU'='Утром я пью чай.';'de-DE'='Am Morgen trinke ich Tee.'}},
        [ordered]@{id=5004;order=4;translations=[ordered]@{'ru-RU'='Потом я учу немецкий.';'de-DE'='Dann lerne ich Deutsch.'}}) },
    [ordered]@{ id=6; key='market_visit'; titles=[ordered]@{'ru-RU'='На рынке';'de-DE'='Auf dem Markt'}; kind='Everyday'; level='A1'; topic='shopping'; segments=@(
        [ordered]@{id=6001;order=1;translations=[ordered]@{'ru-RU'='В субботу Павел идёт на рынок.';'de-DE'='Am Samstag geht Pawel auf den Markt.'}},
        [ordered]@{id=6002;order=2;translations=[ordered]@{'ru-RU'='Он покупает яблоки, хлеб и сыр.';'de-DE'='Er kauft Äpfel, Brot und Käse.'}},
        [ordered]@{id=6003;order=3;translations=[ordered]@{'ru-RU'='Продавщица говорит ему цену.';'de-DE'='Die Verkäuferin nennt ihm den Preis.'}},
        [ordered]@{id=6004;order=4;translations=[ordered]@{'ru-RU'='Павел платит и благодарит её.';'de-DE'='Pawel bezahlt und bedankt sich bei ihr.'}}) },
    [ordered]@{ id=1; key='star_taler'; titles=[ordered]@{'ru-RU'='Звёздные талеры';'de-DE'='Die Sterntaler'}; kind='FairyTale'; level='A2'; topic='fairy-tale'; segments=@(
        [ordered]@{id=1001;order=1;translations=[ordered]@{'ru-RU'='Жила-была бедная девочка, у которой не осталось ни отца, ни матери.';'de-DE'='Es war einmal ein armes Mädchen, das weder Vater noch Mutter hatte.'}},
        [ordered]@{id=1002;order=2;translations=[ordered]@{'ru-RU'='Но она была добра и делилась последним хлебом с теми, кто был голоден.';'de-DE'='Aber sie war gut und teilte ihr letztes Brot mit allen, die Hunger hatten.'}},
        [ordered]@{id=1003;order=3;translations=[ordered]@{'ru-RU'='Ночью звёзды упали с неба и превратились в серебряные монеты.';'de-DE'='In der Nacht fielen die Sterne vom Himmel und wurden zu silbernen Talern.'}},
        [ordered]@{id=1004;order=4;translations=[ordered]@{'ru-RU'='С тех пор девочке больше не приходилось голодать.';'de-DE'='Von da an musste das Mädchen nie wieder Hunger leiden.'}}) },
    [ordered]@{ id=2; key='berlin_morning'; titles=[ordered]@{'ru-RU'='Утро в Берлине';'de-DE'='Ein Morgen in Berlin'}; kind='Everyday'; level='B1'; topic='city'; segments=@(
        [ordered]@{id=2001;order=1;translations=[ordered]@{'ru-RU'='Каждое утро Марина едет на работу на трамвае и читает новости.';'de-DE'='Jeden Morgen fährt Marina mit der Straßenbahn zur Arbeit und liest die Nachrichten.'}},
        [ordered]@{id=2002;order=2;translations=[ordered]@{'ru-RU'='Сегодня транспорт задерживается, потому что в центре ремонтируют дорогу.';'de-DE'='Heute verspätet sich der Verkehr, weil im Zentrum eine Straße repariert wird.'}},
        [ordered]@{id=2003;order=3;translations=[ordered]@{'ru-RU'='Она выходит на остановку раньше и идёт оставшийся путь пешком.';'de-DE'='Sie steigt eine Haltestelle früher aus und geht den restlichen Weg zu Fuß.'}},
        [ordered]@{id=2004;order=4;translations=[ordered]@{'ru-RU'='Так у неё остаётся время выпить кофе перед первой встречей.';'de-DE'='So bleibt ihr noch Zeit, vor der ersten Besprechung einen Kaffee zu trinken.'}}) },
    [ordered]@{ id=3; key='sea_journey'; titles=[ordered]@{'ru-RU'='Путь через море';'de-DE'='Der Weg über das Meer'}; kind='Classic'; level='B2'; topic='journey'; segments=@(
        [ordered]@{id=3001;order=1;translations=[ordered]@{'ru-RU'='На рассвете корабль покинул тихую гавань, и город медленно исчез в тумане.';'de-DE'='Bei Tagesanbruch verließ das Schiff den stillen Hafen, und die Stadt verschwand langsam im Nebel.'}},
        [ordered]@{id=3002;order=2;translations=[ordered]@{'ru-RU'='Путники молчали, словно каждый пытался сохранить в памяти последний образ берега.';'de-DE'='Die Reisenden schwiegen, als versuche jeder, das letzte Bild der Küste im Gedächtnis zu bewahren.'}},
        [ordered]@{id=3003;order=3;translations=[ordered]@{'ru-RU'='К полудню ветер усилился и поднял волны, которые с грохотом разбивались о борт.';'de-DE'='Gegen Mittag wurde der Wind stärker und türmte Wellen auf, die donnernd gegen den Rumpf schlugen.'}},
        [ordered]@{id=3004;order=4;translations=[ordered]@{'ru-RU'='И всё же горизонт впереди казался обещанием, а не угрозой.';'de-DE'='Und doch erschien der Horizont vor ihnen wie ein Versprechen und nicht wie eine Drohung.'}}) },
    [ordered]@{ id=4; key='odyssey_echo'; titles=[ordered]@{'ru-RU'='Эхо возвращения';'de-DE'='Das Echo der Heimkehr'}; kind='Classic'; level='C1'; topic='literature'; segments=@(
        [ordered]@{id=4001;order=1;translations=[ordered]@{'ru-RU'='Много лет он носил в себе образ родного острова, который становился яснее с каждой пройденной чужой землёй.';'de-DE'='Viele Jahre trug er das Bild seiner Heimatinsel in sich, das mit jedem durchquerten fremden Land deutlicher wurde.'}},
        [ordered]@{id=4002;order=2;translations=[ordered]@{'ru-RU'='Бури отнимали у него спутников, но не могли лишить его памяти о доме.';'de-DE'='Die Stürme raubten ihm seine Gefährten, vermochten ihm jedoch nicht die Erinnerung an sein Zuhause zu nehmen.'}},
        [ordered]@{id=4003;order=3;translations=[ordered]@{'ru-RU'='Когда берег наконец возник из сумерек, радость смешалась с осторожностью человека, слишком часто обманутого надеждой.';'de-DE'='Als die Küste endlich aus der Dämmerung auftauchte, mischte sich die Freude mit der Vorsicht eines Mannes, den die Hoffnung zu oft getäuscht hatte.'}},
        [ordered]@{id=4004;order=4;translations=[ordered]@{'ru-RU'='Он понял, что возвращение — это не конец пути, а встреча с тем, кем он стал в дороге.';'de-DE'='Er begriff, dass die Heimkehr nicht das Ende des Weges war, sondern die Begegnung mit dem Menschen, zu dem er unterwegs geworden war.'}}) },
    [ordered]@{ id=7; key='language_and_memory'; titles=[ordered]@{'ru-RU'='Язык и память';'de-DE'='Sprache und Erinnerung'}; kind='Classic'; level='C2'; topic='reflection'; segments=@(
        [ordered]@{id=7001;order=1;translations=[ordered]@{'ru-RU'='Воспоминание редко возвращается в неизменном виде; каждый рассказ незаметно перестраивает то, что претендует лишь сохранить.';'de-DE'='Eine Erinnerung kehrt selten unverändert zurück; jede Erzählung ordnet unmerklich neu, was sie lediglich zu bewahren beansprucht.'}},
        [ordered]@{id=7002;order=2;translations=[ordered]@{'ru-RU'='Язык не только описывает пережитое, но и определяет, какие его оттенки вообще становятся доступными сознанию.';'de-DE'='Sprache beschreibt das Erlebte nicht nur, sondern bestimmt auch, welche seiner Nuancen dem Bewusstsein überhaupt zugänglich werden.'}},
        [ordered]@{id=7003;order=3;translations=[ordered]@{'ru-RU'='Поэтому смена языка способна изменить дистанцию между человеком и его прошлым, не изменяя самих событий.';'de-DE'='Deshalb kann ein Sprachwechsel die Distanz zwischen einem Menschen und seiner Vergangenheit verändern, ohne die Ereignisse selbst zu verändern.'}},
        [ordered]@{id=7004;order=4;translations=[ordered]@{'ru-RU'='В этой едва заметной перестройке раскрывается парадокс памяти: верность ей требует постоянного нового толкования.';'de-DE'='In dieser kaum merklichen Neuordnung zeigt sich das Paradox der Erinnerung: Treue zu ihr verlangt fortwährende neue Deutung.'}}) }
)

$catalog.grammarTasks = @(
    [ordered]@{id=5;key='basic_sentence';level='A0';sourceText='ich / Lena / heiße';instructions=[ordered]@{'ru-RU'='Соберите простое немецкое предложение из слов.';'de-DE'='Bilde aus den Wörtern einen einfachen Satz.'};markerRule='basic-sentence'},
    [ordered]@{id=6;key='simple_negation';level='A1';sourceText='Ich habe einen Hund. Der Hund ist groß.';instructions=[ordered]@{'ru-RU'='Отрицайте обе фразы с kein или nicht.';'de-DE'='Verneine beide Sätze mit kein oder nicht.'};markerRule='negation'},
    [ordered]@{id=1;key='perfekt_weekend';level='A2';sourceText='Am Samstag besuche ich meine Freundin. Wir kochen zusammen und sehen einen Film.';instructions=[ordered]@{'ru-RU'='Перескажите текст в Perfekt.';'de-DE'='Erzähle den Text im Perfekt nach.'};markerRule='perfekt'},
    [ordered]@{id=2;key='passiv_letter';level='B1';sourceText='Die Mitarbeiter schreiben den Bericht und schicken ihn an die Kundin.';instructions=[ordered]@{'ru-RU'='Перепишите текст в Passiv.';'de-DE'='Schreibe den Text im Passiv.'};markerRule='passiv'},
    [ordered]@{id=3;key='konjunktiv_trip';level='B2';sourceText='Ich habe mehr Zeit. Ich reise durch Deutschland und besuche meine Freunde.';instructions=[ordered]@{'ru-RU'='Перескажите как нереальное желание с Konjunktiv II.';'de-DE'='Formuliere einen irrealen Wunsch mit Konjunktiv II.'};markerRule='konjunktiv2'},
    [ordered]@{id=4;key='indirect_speech';level='B2';sourceText='Anna sagt: „Ich bin müde und kann heute nicht kommen.“';instructions=[ordered]@{'ru-RU'='Передайте высказывание в косвенной речи.';'de-DE'='Gib die Aussage in indirekter Rede wieder.'};markerRule='indirekte-rede'},
    [ordered]@{id=7;key='nominalisation_policy';level='C1';sourceText='Die Stadt erweitert den Nahverkehr, damit weniger Menschen mit dem Auto fahren.';instructions=[ordered]@{'ru-RU'='Переформулируйте высказывание в номинальном стиле.';'de-DE'='Formulieren Sie die Aussage im Nominalstil um.'};markerRule='nominalisierung'},
    [ordered]@{id=8;key='participial_style';level='C2';sourceText='Die Ergebnisse wurden sorgfältig geprüft. Danach wurden sie in einem Bericht veröffentlicht.';instructions=[ordered]@{'ru-RU'='Объедините высказывания, используя причастный оборот или расширенное определение.';'de-DE'='Verbinden Sie die Aussagen mit einer Partizipialkonstruktion oder einem erweiterten Attribut.'};markerRule='partizipialattribut'}
)

$json = $catalog | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText($catalogPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Output "Generated $($catalog.words.Count) words, $($catalog.sentences.Count) sentences, $($catalog.passages.Count) passages, $($catalog.grammarTasks.Count) grammar tasks."
