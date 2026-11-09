import { ArticleEditorPage } from '../../../../components/Articles';

export default async function Page({ params }: { params: Promise<{ articleId: string }> }) {
  const { articleId } = await params;
  return <ArticleEditorPage articleId={articleId} />;
}
