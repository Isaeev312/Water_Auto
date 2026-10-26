;;; wa.lsp — команды AutoCAD для работы с Water_Auto (README §7, tools/autocad/README.md).
;;; Лицензия MIT, как у всего репозитория.
;;; Файл в UTF-8: AutoCAD 2021 и новее (системная переменная LISPSYS = 1).
;;; Чисел норм здесь нет: проверки по СП 485 выполняет программа, команды только готовят и проверяют чертёж.
;;;
;;;   WA-SETUP  — создать слои соглашения и выхода программы, единицы — мм.
;;;   WA-CHECK  — проверить чертёж перед расчётом: замкнутость контуров, высоту балок, наличие ввода, единицы.
;;;   WA-INSERT — вставить DWG, выданный программой, в 0,0 (с удалением прежней вставки по желанию).

(vl-load-com)

;; Слои: имя и цвет. Цвета выхода совпадают с PlanDxfWriter и AxonometryDxfWriter.
(setq wa:*input-layers*
  '(("ПТ_КОНТУР" . 3) ("ПТ_КОЛОННЫ" . 8) ("ПТ_БАЛКИ" . 30)
    ("ПТ_КОРОБА" . 40) ("ПТ_ЗАПРЕТ" . 210) ("ПТ_ВВОД" . 5)))

(setq wa:*output-layers*
  '(("ПТ_ОРОСИТЕЛИ" . 1) ("ПТ_ТРУБЫ" . 4) ("ПТ_УЧАСТКИ" . 7) ("ПТ_ЗАМЕЧАНИЯ" . 6)
    ("ПТ_ДИКТ_ЗОНА" . 2) ("ПТ_РАСЧЁТ" . 7)
    ("ПТ_АКСОН_ТРУБЫ" . 4) ("ПТ_АКСОН_ОРОСИТЕЛИ" . 1) ("ПТ_АКСОН_ПОДПИСИ" . 7)
    ("ПТ_АКСОН_ОТМЕТКИ" . 7) ("ПТ_ФОРМАТ" . 7)))

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

  (setq contours (wa:check-closed "ПТ_КОНТУР" '("TEXT" "MTEXT") "Контур"))
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

(princ "\nWater_Auto: команды WA-SETUP, WA-CHECK, WA-INSERT загружены.")
(princ)
