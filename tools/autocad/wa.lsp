;;; wa.lsp — команды AutoCAD для работы с Water_Auto (README §7, tools/autocad/README.md).
;;; Лицензия MIT, как у всего репозитория.
;;; Файл в UTF-8: AutoCAD 2021 и новее (системная переменная LISPSYS = 1).
;;; Чисел норм здесь нет: проверки по СП 485 выполняет программа, команды только готовят и проверяют чертёж.
;;;
;;;   WA-SETUP  — создать слои соглашения и выхода программы, единицы — мм.
;;;   WA-CHECK  — проверить чертёж перед расчётом: замкнутость контуров, высоту балок, наличие ввода, единицы.
;;;   WA-INSERT — вставить DWG, выданный программой, в 0,0 (с удалением прежней вставки по желанию).
;;;   WA-ROOM   — обвести помещение щелчком внутри (BOUNDARY -> ПТ_КОНТУР) и подписать имя.
;;;   WA-INLET  — точки ввода на ПТ_ВВОД; WA-BEAM — балка по оси с шириной и высотой; WA-ZONE — зона без оросителей.

(vl-load-com)

;; Слои: имя и цвет. Цвета выхода совпадают с PlanDxfWriter и AxonometryDxfWriter.
(setq wa:*input-layers*
  '(("ПТ_КОНТУР" . 3) ("ПТ_КОЛОННЫ" . 8) ("ПТ_БАЛКИ" . 30)
    ("ПТ_КОРОБА" . 40) ("ПТ_ЗАПРЕТ" . 210) ("ПТ_ВВОД" . 5) ("ПТ_НС" . 6) ("ПТ_НС_ВВОД" . 5) ("ПТ_НС_ВЫПУСК" . 1) ("ПТ_НС_ГМ" . 1) ("ПТ_ВВОД_ЗД" . 5) ("ПТ_ОТМЕТКИ" . 2)))

(setq wa:*output-layers*
  '(("ПТ_ОРОСИТЕЛИ" . 1) ("ПТ_ТРУБЫ" . 4) ("ПТ_УЧАСТКИ" . 7) ("ПТ_ЗАМЕЧАНИЯ" . 6)
    ("ПТ_ДИКТ_ЗОНА" . 2) ("ПТ_РАСЧЁТ" . 7)
    ("ПТ_АКСОН_ТРУБЫ" . 4) ("ПТ_АКСОН_ОРОСИТЕЛИ" . 1) ("ПТ_АКСОН_ПОДПИСИ" . 7)
    ("ПТ_АКСОН_ОТМЕТКИ" . 7) ("ПТ_АКСОН_РАСЧЁТ" . 4) ("ПТ_АКСОН_ДИКТ_ЗОНА" . 1) ("ПТ_ФОРМАТ" . 7)))

(setq wa:*check-layer* "ПТ_ПРОВЕРКА")

;; Создать слой, если его нет. Возвращает не nil, если слой создан.
(defun wa:layer (name color)
  (if (not (tblsearch "LAYER" name))
    (entmake (list '(0 . "LAYER")
                   '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbLayerTableRecord")
                   (cons 2 name)
                   '(70 . 0)
                   (cons 62 color)
                   '(6 . "Continuous")))))

;; Объекты слоя в пространстве модели (или nil).
(defun wa:on-layer (name)
  (ssget "_X" (list (cons 8 name) '(410 . "Model"))))

(defun wa:3d (p) (list (car p) (cadr p) (if (caddr p) (caddr p) 0.0)))

;; Пометка: кружок и текст на слое проверки, строка в командной строке.
(defun wa:mark (pt msg)
  (setq pt (wa:3d pt))
  (entmake (list '(0 . "CIRCLE") (cons 8 wa:*check-layer*) (cons 10 pt) '(40 . 500.0)))
  (entmake (list '(0 . "TEXT") (cons 8 wa:*check-layer*)
                 (cons 10 (polar pt 0.0 600.0)) '(40 . 250.0) (cons 1 msg)))
  (setq wa:*marks* (1+ wa:*marks*))
  (princ (strcat "\n  " msg " — X " (rtos (car pt) 2 0) ", Y " (rtos (cadr pt) 2 0))))

;; Вершины LWPOLYLINE.
(defun wa:vertices (d / out)
  (foreach x d (if (= (car x) 10) (setq out (cons (cdr x) out))))
  (reverse out))

;; Замкнута ли полилиния: флаг «Замкнуто» или совпадение первой и последней вершины (допуск 1 мм).
(defun wa:closed-p (d / v)
  (setq v (wa:vertices d))
  (or (= 1 (logand 1 (cdr (assoc 70 d))))
      (and (> (length v) 2) (< (distance (car v) (last v)) 1.0))))

;; Проверка замкнутых полилиний слоя; допустимые прочие типы объектов — в списке ok-types.
(defun wa:check-closed (layer ok-types what / ss i e d type)
  (if (setq ss (wa:on-layer layer))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) d (entget e) type (cdr (assoc 0 d)) i (1+ i))
        (cond
          ((= type "LWPOLYLINE")
           (if (not (wa:closed-p d))
             (wa:mark (cdr (assoc 10 d)) (strcat what " — полилиния не замкнута"))))
          ((not (member type ok-types))
           (wa:mark (cdr (assoc 10 d)) (strcat what " — объект " type " не читается программой")))))))
  ss)

(defun c:WA-SETUP (/ n)
  (setq n 0)
  (foreach l (append wa:*input-layers* wa:*output-layers*)
    (if (wa:layer (car l) (cdr l)) (setq n (1+ n))))
  (setvar "INSUNITS" 4)
  (princ (strcat "\nWater_Auto: создано слоёв — " (itoa n) "; единицы чертежа — мм."))
  (princ))

(defun c:WA-CHECK (/ old contours ss i d)
  (setq wa:*marks* 0)
  (wa:layer wa:*check-layer* 6)
  (if (setq old (wa:on-layer wa:*check-layer*)) (command "_.ERASE" old ""))
  (princ "\nWater_Auto: проверка чертежа перед расчётом")

  (if (/= (getvar "INSUNITS") 4)
    (progn (setq wa:*marks* (1+ wa:*marks*))
           (princ "\n  Единицы чертежа не мм (INSUNITS ≠ 4) — выполните WA-SETUP или задайте единицы.")))

  (setq contours (wa:check-closed "ПТ_КОНТУР" '("TEXT" "MTEXT" "LINE") "Контур"))
  (if (null contours)
    (progn (setq wa:*marks* (1+ wa:*marks*))
           (princ "\n  На слое ПТ_КОНТУР нет объектов.")))

  (wa:check-closed "ПТ_КОЛОННЫ" '("CIRCLE" "INSERT") "Колонна")
  (wa:check-closed "ПТ_КОРОБА" '() "Короб")
  (wa:check-closed "ПТ_ЗАПРЕТ" '() "Зона без оросителей")

  ;; Балка: «Толщина» полилинии — высота балки (README §7).
  (if (setq ss (wa:on-layer "ПТ_БАЛКИ"))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq d (entget (ssname ss i)) i (1+ i))
        (if (and (= (cdr (assoc 0 d)) "LWPOLYLINE")
                 (or (null (assoc 39 d)) (<= (cdr (assoc 39 d)) 0.0)))
          (wa:mark (cdr (assoc 10 d)) "Балка — не задана толщина (высота балки)")))))
  (wa:check-closed "ПТ_БАЛКИ" '() "Балка")

  (if (null (wa:on-layer "ПТ_ВВОД"))
    (progn (setq wa:*marks* (1+ wa:*marks*))
           (princ "\n  Нет точки на слое ПТ_ВВОД: в каждом защищаемом помещении нужна своя.")))

  (princ (if (= wa:*marks* 0)
           "\nЗамечаний нет. Самопересечения и расположение объектов проверит программа при импорте."
           (strcat "\nЗамечаний: " (itoa wa:*marks* ) " (пометки на слое " wa:*check-layer* ").")))
  (princ))

(defun c:WA-INSERT (/ path answer ss)
  (setq path (getfiled "Чертёж Water_Auto (DWG)" "" "dwg" 0))
  (if path
    (progn
      (initget "Да Нет")
      (setq answer (getkword "\nУдалить прежние объекты слоёв выхода Water_Auto? [Да/Нет] <Нет>: "))
      (if (= answer "Да")
        (foreach l wa:*output-layers*
          (if (setq ss (wa:on-layer (car l))) (command "_.ERASE" ss ""))))
      ;; «*» — вставка с расчленением: объекты попадают на свои слои, координаты чертежа сохраняются.
      (command "_.-INSERT" (strcat "*" path) "_non" "0,0" 1 0)
      (princ (strcat "\nВставлено в 0,0: " path ". ID оросителей — скрытый атрибут, показать: ATTDISP = Вкл.")))
    (princ "\nФайл не выбран."))
  (princ))

;; ---------- Помощники отрисовки: обвести помещение, поставить ввод, начертить балку, зону запрета ----------

;; Последний созданный объект после команды (или nil, если команда ничего не создала).
(defun wa:last-after (before / e)
  (setq e (entlast))
  (if (and e (not (equal e before))) e))

;; Перенести объект на слой.
(defun wa:to-layer (e layer / d)
  (setq d (entget e))
  (entmod (subst (cons 8 layer) (assoc 8 d) d)))

;; Высота текста подписи: 2,5 мм листа в масштабе DIMSCALE (в модели 1:100 — 250 мм).
(defun wa:text-height () (* 2.5 (max 1.0 (getvar "DIMSCALE"))))

(defun c:WA-ROOM (/ pt before e name n)
  ;; Щелчок внутри помещения архитектурного плана: BOUNDARY строит замкнутую полилинию по стенам,
  ;; она уходит на ПТ_КОНТУР, имя помещения — текстом в точке щелчка (по нему программа находит группу, подвод и т. п.).
  (wa:layer "ПТ_КОНТУР" 3)
  (setq n 0)
  (while (setq pt (getpoint "\nТочка внутри помещения <выход>: "))
    (setq before (entlast))
    (command "_.-BOUNDARY" "_non" pt "")
    (if (setq e (wa:last-after before))
      (progn
        (wa:to-layer e "ПТ_КОНТУР")
        (setq name (getstring T "\nИмя помещения (как в экспликации): "))
        (if (/= name "")
          (entmake (list '(0 . "TEXT") '(8 . "ПТ_КОНТУР") (cons 10 (wa:3d pt)) (cons 11 (wa:3d pt))
                         (cons 40 (wa:text-height)) (cons 1 name) '(72 . 1) '(73 . 2))))
        (setq n (1+ n)))
      (princ "\n  Контур не найден: помещение не замкнуто стенами — обведите полилинией на слое ПТ_КОНТУР вручную.")))
  (princ (strcat "\nWater_Auto: обведено помещений — " (itoa n) "."))
  (princ))

(defun c:WA-INLET (/ pt n)
  ;; Точка ввода (стояк, узел управления): своя в каждом защищаемом помещении (README §7).
  (wa:layer "ПТ_ВВОД" 5)
  (setq n 0)
  (while (setq pt (getpoint "\nТочка ввода питающего трубопровода в помещение <выход>: "))
    (entmake (list '(0 . "POINT") '(8 . "ПТ_ВВОД") (cons 10 (wa:3d pt))))
    (setq n (1+ n)))
  (princ (strcat "\nWater_Auto: точек ввода — " (itoa n) ". Видны при PDMODE = 34."))
  (princ))

(defun c:WA-BEAM (/ h z p1 p2 w a v n)
  ;; Балка по оси: две точки, ширина и высота, мм. Толщина полилинии = высота балки, Уровень = отметка низа перекрытия (README §7).
  (wa:layer "ПТ_БАЛКИ" 30)
  (setq n 0)
  (if (and (setq w (getdist "\nШирина балки, мм: "))
           (setq h (getdist "\nВысота балки (от низа перекрытия), мм: "))
           (setq z (getreal "\nОтметка низа перекрытия (абсолютная, как отметки ПТ_ОТМЕТКИ), мм, например -1200: ")))
    (while (and (setq p1 (getpoint "\nНачало оси балки <выход>: ")) (setq p2 (getpoint p1 "\nКонец оси балки: ")))
      (setq a (+ (angle p1 p2) (/ pi 2))
            v (list (polar p1 a (/ w 2)) (polar p2 a (/ w 2)) (polar p2 a (/ w -2)) (polar p1 a (/ w -2))))
      (if (entmake (append (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") '(8 . "ПТ_БАЛКИ") '(100 . "AcDbPolyline")
                                 '(90 . 4) '(70 . 1) (cons 38 z) (cons 39 h))
                           (mapcar '(lambda (p) (cons 10 (list (car p) (cadr p)))) v)))
        (setq n (1+ n)))))
  (princ (strcat "\nWater_Auto: балок — " (itoa n) "."))
  (princ))

(defun c:WA-ZONE (/ before e)
  ;; Зона без оросителей (электрощитовая и т. п.): замкнутая полилиния на ПТ_ЗАПРЕТ; подпись «ПНС» — помещение насосной.
  (wa:layer "ПТ_ЗАПРЕТ" 210)
  (setq before (entlast))
  (princ "\nПостройте замкнутую полилинию зоны (последний сегмент — «Замкнуть»).")
  (command "_.PLINE")
  (while (> (getvar "CMDACTIVE") 0) (command pause))
  (if (setq e (wa:last-after before))
    (progn (wa:to-layer e "ПТ_ЗАПРЕТ") (princ "\nWater_Auto: зона без оросителей добавлена."))
    (princ "\nПолилиния не построена."))
  (princ))

(princ "\nWater_Auto: команды WA-SETUP, WA-CHECK, WA-INSERT, WA-ROOM, WA-INLET, WA-BEAM, WA-ZONE загружены.")
(princ)
