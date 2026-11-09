-- Dữ liệu mẫu CHỈ cho máy dev (lệnh `seed-dev` của Tinhte.DbMigrator — từ chối ngoài Development).
-- Mục đích: có nội dung thật trong DB để xem trang chủ public (UX-PUB-01), danh sách/hàng đợi duyệt trên CMS.
--
-- Đi đúng luồng như ứng dụng (nháp → gửi duyệt → duyệt → xuất bản) để trigger 0320/0324/0307 vẫn kiểm như thật.
-- Lối tắt duy nhất: rights_status = CLEARED (giống lệnh clear-rights — chưa có quy trình xác nhận quyền, WBS-023).
-- Không seed bài tài trợ: cờ ads.sponsored_publication mặc định OFF và không được bật (CLAUDE.md §3).
-- Không seed video: sẽ bổ sung khi nhánh video nổi bật được hợp nhất với migration video hiện tại.
--
-- Idempotent: đã seed (có user mẫu) thì bỏ qua. Muốn seed lại: tạo DB mới (PGlite khởi động lại là DB trống).
-- ID cố định dạng 00000000-0000-4000-8xxx-… để dễ nhận ra dữ liệu mẫu.

DO $$
DECLARE
  v_author   uuid := '00000000-0000-4000-8001-000000000001';
  v_author2  uuid := '00000000-0000-4000-8001-000000000002';
  v_approver uuid := '00000000-0000-4000-8001-000000000003';
  v_site     text := 'www';
  r          record;
  v_article  uuid;
  v_rev      uuid;
  v_cat      uuid;
  v_at       timestamptz;
  v_count    int := 0;
BEGIN
  IF EXISTS (SELECT 1 FROM tt.users WHERE id = v_author) THEN
    RAISE NOTICE 'dev seed: already applied (user % exists) — skipped', v_author;
    RETURN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM tt.sites WHERE key = v_site) THEN
    RAISE EXCEPTION 'dev seed: site % is missing in tt.sites — run `up` first (it syncs the Site Registry)', v_site;
  END IF;

  PERFORM set_config('tt.actor_id', v_approver::text, true);

  INSERT INTO tt.users (id, display_name) VALUES
    (v_author, 'Minh Anh (mẫu)'), (v_author2, 'Quốc Bảo (mẫu)'), (v_approver, 'Thu Hà — duyệt (mẫu)');

  INSERT INTO tt.categories (id, slug, name, site_key) VALUES
    ('00000000-0000-4000-8002-000000000001', 'dien-thoai', 'Điện thoại', v_site),
    ('00000000-0000-4000-8002-000000000002', 'may-tinh', 'Máy tính', v_site),
    ('00000000-0000-4000-8002-000000000003', 'am-thanh', 'Âm thanh', v_site),
    ('00000000-0000-4000-8002-000000000004', 'nha-thong-minh', 'Nhà thông minh', v_site),
    ('00000000-0000-4000-8002-000000000005', 'xe', 'Xe', v_site),
    ('00000000-0000-4000-8002-000000000006', 'thu-thuat', 'Thủ thuật', v_site),
    ('00000000-0000-4000-8002-000000000007', 'luu-tru', 'Lưu trữ (đã tắt)', v_site)
  ON CONFLICT (slug) DO NOTHING;
  UPDATE tt.categories SET is_enabled = false WHERE id = '00000000-0000-4000-8002-000000000007';

  -- state: PUBLISHED | DRAFT | IN_REVIEW | UNPUBLISHED (đã public rồi bị gỡ — không được hiện ở trang chủ)
  FOR r IN
    SELECT * FROM (VALUES
      (1,  'trai-nghiem-dien-thoai-gap-moi', 'Trải nghiệm nhanh mẫu điện thoại gập mới: bản lề chắc hơn, nếp gấp gần như biến mất',
           'Sau một tuần dùng làm máy chính, đây là những điểm cải thiện rõ nhất và vài thứ vẫn cần hoàn thiện.', 1, 35, 'PUBLISHED'),
      (2,  'so-sanh-pin-5-laptop-mong-nhe', 'So sánh pin 5 laptop mỏng nhẹ phổ biến nhất cuối năm',
           'Bài đo xem video liên tục ở độ sáng 200 nit, cùng cấu hình mạng và âm lượng.', 2, 120, 'PUBLISHED'),
      (3,  'bat-xac-thuc-hai-lop', 'Hướng dẫn bật xác thực hai lớp cho mọi tài khoản quan trọng',
           'Mất 10 phút, tránh được phần lớn rủi ro mất tài khoản.', 6, 180, 'PUBLISHED'),
      (4,  'sac-nhanh-100w-co-chai-pin', 'Sạc nhanh 100W có làm chai pin điện thoại nhanh hơn?',
           'Chúng tôi đo nhiệt độ và dung lượng sau 300 chu kỳ sạc.', 1, 260, 'PUBLISHED'),
      (5,  'wifi-7-co-dang-nang-cap', 'Wi-Fi 7 đã đáng nâng cấp cho gia đình chưa?',
           'Tốc độ thực tế khác xa con số trên hộp.', 4, 400, 'PUBLISHED'),
      (6,  'cap-nhat-he-dieu-hanh-thang-11', 'Cập nhật hệ điều hành tháng 11 sửa lỗi hao pin khi chờ',
           'Bản vá đã phát hành cho các dòng máy từ 2023 trở lên.', 1, 8, 'PUBLISHED'),
      (7,  'tai-nghe-chong-on-tam-trung', 'Tai nghe chống ồn giá tầm trung: 3 lựa chọn đáng tiền',
           'Chất âm, độ ồn và thời lượng pin được đo trong cùng điều kiện.', 3, 22, 'PUBLISHED'),
      (8,  'chup-anh-dem-khong-chan-may', 'Mẹo chụp ảnh đêm bằng điện thoại không cần chân máy',
           'Giữ máy đúng cách và chọn thời gian phơi sáng hợp lý.', 6, 47, 'PUBLISHED'),
      (9,  'ssd-gan-ngoai-cong-ket-noi', 'Ổ SSD gắn ngoài tốc độ cao: cổng kết nối quan trọng hơn bạn nghĩ',
           'Cùng một ổ, tốc độ chênh gấp ba lần tùy cổng.', 2, 75, 'PUBLISHED'),
      (10, 'dong-ho-do-huyet-ap', 'Đồng hồ thông minh đo huyết áp: chính xác đến đâu?',
           'Đối chiếu với máy đo y tế trong hai tuần.', 1, 130, 'PUBLISHED'),
      (11, 'don-dung-luong-dam-may', 'Cách dọn dung lượng iCloud và Google Drive hiệu quả',
           'Tìm và xóa tệp lớn, ảnh trùng, bản sao lưu cũ.', 6, 200, 'PUBLISHED'),
      (12, 'man-hinh-4k-27-inch-144hz', 'Màn hình 4K 27 inch cho dân văn phòng: có cần 144Hz?',
           'Khi nào tần số quét cao thực sự có ích.', 2, 320, 'PUBLISHED'),
      (13, 'robot-hut-bui-tu-giat-gie', 'Robot hút bụi tự giặt giẻ: điểm cộng và điểm trừ sau 1 tháng',
           'Ồn, tốn nước hay thực sự tiện?', 4, 600, 'PUBLISHED'),
      (14, 'bao-mat-router-gia-dinh', 'Bảo mật router gia đình: 6 việc nên làm ngay',
           'Đổi mật khẩu quản trị mới chỉ là bước đầu.', 4, 1500, 'PUBLISHED'),
      (15, 'xe-dien-co-nho-chi-phi-sac', 'Xe điện cỡ nhỏ: chi phí sạc thực tế mỗi tháng',
           'Tính theo giá điện bậc thang và quãng đường đi làm.', 5, 3000, 'PUBLISHED'),
      (16, 'ban-phim-co-cho-nguoi-moi', 'Bàn phím cơ cho người mới: chọn switch nào?',
           'Linear, tactile hay clicky — khác nhau ở đâu khi gõ cả ngày.', 2, 4300, 'PUBLISHED'),
      (17, 'loa-thong-minh-tieng-viet', 'Loa thông minh hiểu tiếng Việt: thử 4 mẫu phổ biến',
           'Ra lệnh bật đèn, hẹn giờ, hỏi thời tiết — mẫu nào hiểu đúng nhất?', 3, 5800, 'PUBLISHED'),
      (18, 'danh-gia-may-tinh-bang-gia-re', 'Đánh giá máy tính bảng giá rẻ cho học sinh',
           'Bản nháp — chưa gửi duyệt.', 2, 0, 'DRAFT'),
      (19, 'camera-an-ninh-khong-day', 'Camera an ninh không dây: lưu trữ đám mây hay thẻ nhớ?',
           'Đang chờ duyệt — dùng để thử hàng đợi duyệt trên CMS.', 4, 0, 'IN_REVIEW'),
      (20, 'tin-da-go-thu-nghiem', 'Tin đã gỡ (thử nghiệm): không được hiện ở trang chủ',
           'Bài từng xuất bản rồi bị gỡ — dùng để kiểm FR-DISC-004.', 6, 90, 'UNPUBLISHED')
    ) AS t(n, slug, title, summary, cat, minutes_ago, state)
  LOOP
    v_article := ('00000000-0000-4000-8003-' || lpad(r.n::text, 12, '0'))::uuid;
    v_rev     := ('00000000-0000-4000-8004-' || lpad(r.n::text, 12, '0'))::uuid;
    v_cat     := ('00000000-0000-4000-8002-' || lpad(r.cat::text, 12, '0'))::uuid;
    v_at      := now() - make_interval(mins => r.minutes_ago);

    INSERT INTO tt.articles (id, slug, status, category_id, owner_id, primary_site_key, created_at)
    VALUES (v_article, r.slug, 'DRAFT', v_cat, CASE WHEN r.n % 2 = 0 THEN v_author2 ELSE v_author END, v_site,
            v_at - interval '2 hours');
    INSERT INTO tt.article_revisions (id, article_id, number, title, summary, category_id, author_id, content_kind, body, created_at)
    VALUES (v_rev, v_article, 1, r.title, r.summary, v_cat, CASE WHEN r.n % 2 = 0 THEN v_author2 ELSE v_author END, 'EDITORIAL',
            jsonb_build_array(
              jsonb_build_object('type', 'paragraph', 'text', r.summary),
              jsonb_build_object('type', 'heading', 'level', 2, 'text', 'Điểm chính'),
              jsonb_build_object('type', 'paragraph', 'text', 'Nội dung mẫu để dựng giao diện trên máy dev. Không phải bài thật.')),
            v_at - interval '2 hours');
    UPDATE tt.articles SET working_revision_id = v_rev WHERE id = v_article;

    CONTINUE WHEN r.state = 'DRAFT';
    UPDATE tt.article_revisions SET status = 'IN_REVIEW', submitted_by = (SELECT owner_id FROM tt.articles WHERE id = v_article),
           submitted_at = v_at - interval '90 minutes' WHERE id = v_rev;
    UPDATE tt.articles SET status = 'IN_REVIEW' WHERE id = v_article;

    CONTINUE WHEN r.state = 'IN_REVIEW';
    UPDATE tt.article_revisions SET status = 'APPROVED', reviewed_by = v_approver, reviewed_at = v_at - interval '30 minutes' WHERE id = v_rev;
    -- Lối tắt dev: xác nhận quyền (chưa có quy trình WBS-023), rồi xuất bản như ArticleService.PublishAsync.
    UPDATE tt.articles SET rights_status = 'CLEARED' WHERE id = v_article;
    UPDATE tt.articles SET public_revision_id = v_rev, status = 'PUBLISHED', published_at = v_at WHERE id = v_article;
    UPDATE tt.article_revisions SET status = 'PUBLISHED', published_at = v_at, published_by = v_approver WHERE id = v_rev;

    IF r.state = 'UNPUBLISHED' THEN
      UPDATE tt.articles SET status = 'UNPUBLISHED' WHERE id = v_article;
    END IF;
    v_count := v_count + 1;
  END LOOP;

  -- Tin nổi bật do biên tập chọn (0322): bài 4, 1, 3 theo thứ tự này; trang chủ bù bằng bài mới nhất.
  -- Bài 20 (đã gỡ) cũng được ghim để thử: trang chủ phải bỏ qua, CMS hiện "không còn công khai".
  INSERT INTO tt.home_layouts (site_key) VALUES (v_site) ON CONFLICT (site_key) DO NOTHING;
  DELETE FROM tt.home_featured_articles WHERE site_key = v_site;
  INSERT INTO tt.home_featured_articles (site_key, article_id, position) VALUES
    (v_site, '00000000-0000-4000-8003-000000000004', 1),
    (v_site, '00000000-0000-4000-8003-000000000020', 2),
    (v_site, '00000000-0000-4000-8003-000000000001', 3),
    (v_site, '00000000-0000-4000-8003-000000000003', 4);
  UPDATE tt.home_layouts SET updated_by = v_approver WHERE site_key = v_site;

  RAISE NOTICE 'dev seed: 3 users, 7 categories, 20 articles (% published incl. 1 withdrawn, 1 in review, 1 draft), 4 home pins', v_count;
END $$;
