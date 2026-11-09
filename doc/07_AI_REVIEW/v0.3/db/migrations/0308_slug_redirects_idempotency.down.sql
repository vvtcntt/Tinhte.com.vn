-- 0308 down — TỪ CHỐI nếu đã có redirect slug (mất redirect = gãy link/SEO). Khóa idempotency là dữ liệu tạm, được xóa.
SELECT tt_migration.refuse_if_rows('0308', 'article_slug_redirects', 'SEO redirects');
SELECT tt_migration.rollback('0308');
