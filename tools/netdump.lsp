;; NETDUMP -- dump every PCD copper trace so routing can be checked geometrically WITHOUT a full
;; scan tool scan. Read-only: it selects and measures, it never modifies the drawing.
;;
;; PCD draws copper as LWPOLYLINEs on PCD-NET with a constant width (group 43) and an Elevation
;; (group 38), so the copper layer of every segment is readable straight off the entity. That makes
;; a crossing test cheap: seconds, versus a multi-minute ~25 MB scan tool scan.
;;
;; Output: one line per trace,
;;     P <elevation> <width> <trueColor|-1> x,y x,y ...
;; then a trailing "VIAS <n>". Feed the file to tools/cross_check.py.
;;
;; Path: defaults to <TEMPPREFIX>pcd_netdump.txt. Override by setting *pcd-netdump-out* before
;; running, e.g. (setq *pcd-netdump-out* "C:\\some\\where\\dump.txt").

(defun c:NETDUMP ( / f p ss i e el w c s)
  (setq p (if (and (boundp '*pcd-netdump-out*) *pcd-netdump-out*)
            *pcd-netdump-out*
            (strcat (getvar "TEMPPREFIX") "pcd_netdump.txt")))
  (setq f (open p "w"))
  (if (null f)
    (princ (strcat "\nNETDUMP: cannot write " p))
    (progn
      (setq ss (ssget "_X" (list (cons 0 "LWPOLYLINE") (cons 8 "PCD-NET"))))
      (if ss
        (progn
          (setq i 0)
          (while (< i (sslength ss))
            (setq e (entget (ssname ss i)))
            (setq el (cdr (assoc 38 e)))  (if (null el) (setq el 0.0))
            (setq w  (cdr (assoc 43 e)))  (if (null w)  (setq w  0.0))
            (setq c  (cdr (assoc 420 e))) (if (null c)  (setq c  -1))
            (setq s (strcat "P " (rtos el 2 4) " " (rtos w 2 3) " " (itoa c)))
            (foreach x e
              (if (= 10 (car x))
                (setq s (strcat s " " (rtos (cadr x) 2 3) "," (rtos (caddr x) 2 3)))))
            (write-line s f)
            (setq i (1+ i))))
        (write-line "NOPOLY" f))
      (setq ss (ssget "_X" (list (cons 8 "PCD-VIA"))))
      (write-line (strcat "VIAS " (if ss (itoa (sslength ss)) "0")) f)
      (close f)
      (princ (strcat "\nNETDUMP -> " p))))
  (princ))
