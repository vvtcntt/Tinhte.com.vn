-- 0307 down — gỡ trigger chốt chặn. Sau khi down, chỉ còn kiểm tra ở tầng domain.
SELECT tt_migration.rollback('0307');
