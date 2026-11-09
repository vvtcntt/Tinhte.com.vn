// PostgreSQL dev/test KHÔNG cần Docker: PGlite (PostgreSQL 18 biên dịch WASM) + giao thức wire qua TCP.
//   cd tools/pglite-server && npm ci && npm start             (127.0.0.1:54329, dữ liệu trong bộ nhớ)
//   PGLITE_PORT=54329 PGLITE_DATA_DIR=./.data npm start         (giữ dữ liệu giữa các lần chạy)
// Chuỗi kết nối Npgsql (BẮT BUỘC Pooling=false — xem dưới):
//   Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres;Pooling=false
// GIỚI HẠN đã kiểm (09/11/2026, pglite-socket 0.2.13 + Npgsql 10.0.3):
//   - PGlite là MỘT phiên: các kết nối được xếp hàng, transaction chạy tuần tự → không test được khóa/đồng thời.
//   - Sau lỗi SQL, kết nối được tái sử dụng bị lệch giao thức → phải tắt pooling để mỗi lần mở là kết nối mới.
//   - maxConnections > 1 vì server xử lý đóng kết nối cũ bất đồng bộ; để 1 thì kết nối kế tiếp bị từ chối.
// CI dùng PostgreSQL thật (service container) — đây chỉ là đường tắt cho máy dev.
import { PGlite } from '@electric-sql/pglite';
import { PGLiteSocketServer } from '@electric-sql/pglite-socket';

const port = Number(process.env.PGLITE_PORT ?? 54329);
const db = await PGlite.create(process.env.PGLITE_DATA_DIR || undefined);
const server = new PGLiteSocketServer({ db, port, host: '127.0.0.1', maxConnections: 16 });
await server.start();
console.log(`pglite-server listening on 127.0.0.1:${port}`);

const stop = async () => {
  await server.stop();
  await db.close();
  process.exitCode = 0;
};
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
